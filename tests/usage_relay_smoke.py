"""Isolated process test: durable capture while dashboard is closed, then replay/ACK.
No installed services, user settings, models or production data are changed.
"""
import argparse, contextlib, http.server, json, pathlib, secrets, socket, sqlite3
import subprocess, tempfile, threading, time, urllib.request

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

def get(base, path):
    with urllib.request.urlopen(base+path, timeout=2) as r:
        return json.load(r)

def wait_ready(base):
    for _ in range(100):
        try:
            if get(base, '/health')['service']=='AI Pulse observation gateway': return
        except (OSError, ValueError): pass
        time.sleep(.1)
    raise AssertionError('relay did not become ready')

class Backend(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0)))
        self.send_response(200);self.send_header('Content-Type','application/x-ndjson');self.end_headers()
        self.wfile.write(b'{"model":"fixture","done":true,"prompt_eval_count":12,"eval_count":7}\n')

def main():
    p=argparse.ArgumentParser();p.add_argument('executable');args=p.parse_args()
    with tempfile.TemporaryDirectory(prefix='aipulse-relay-smoke-') as temp:
        root=pathlib.Path(temp);backend_port,listen_port=port(),port()
        server=http.server.ThreadingHTTPServer(('127.0.0.1',backend_port),Backend)
        threading.Thread(target=server.serve_forever,daemon=True).start()
        (root/'usage-relay.json').write_text(json.dumps(dict(ListenPort=listen_port,BackendPort=backend_port,LaunchBackend=False,ReadKey=secrets.token_hex(32))))
        (root/'settings.json').write_text(json.dumps(dict(ReadAgentUsage=False,TelemetryPort=0,EnableGateway=False,LastBackup='2099-01-01T00:00:00Z')))
        command=[str(pathlib.Path(args.executable).resolve()),'--usage-relay','--data-dir',temp]
        base=f'http://127.0.0.1:{listen_port}'
        relay=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            wait_ready(base)
            req=urllib.request.Request(base+'/api/generate',data=b'{"model":"fixture","prompt":"SECRET_RELAY_PROMPT"}',headers={'Content-Type':'application/json'})
            with urllib.request.urlopen(req,timeout=5) as response: assert json.load(response)['eval_count']==7
            time.sleep(.2)
            # Simulate abrupt process loss on every OS, not only Windows.
            relay.kill();relay.wait(timeout=10)
            relay=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            wait_ready(base)
            collector=[args.executable,'--collect','--seconds','3','--data-dir',temp]
            for _ in range(2):
                subprocess.run(collector,timeout=30,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            with contextlib.closing(sqlite3.connect(root/'data/pulse.db')) as db:
                assert db.execute("select count(*),sum(input),sum(output) from usage where source='Ollama'").fetchone()==(1,12,7)
            with contextlib.closing(sqlite3.connect(root/'relay/usage.db')) as db:
                assert db.execute("select count(*) from sync_events where kind='usage'").fetchone()[0]==0
            fresh=root/'fresh-dashboard';fresh.mkdir()
            (fresh/'usage-relay.json').write_bytes((root/'usage-relay.json').read_bytes())
            (fresh/'settings.json').write_bytes((root/'settings.json').read_bytes())
            subprocess.run([args.executable,'--collect','--seconds','3','--data-dir',str(fresh)],timeout=30,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            with contextlib.closing(sqlite3.connect(fresh/'data/pulse.db')) as db:
                assert db.execute("select count(*),sum(input),sum(output) from usage where source='Ollama'").fetchone()==(1,12,7),'Retained relay baseline must repair a fresh/restored dashboard even after ACK'
            assert b'SECRET_RELAY_PROMPT' not in (root/'data/pulse.db').read_bytes()
            assert b'SECRET_RELAY_PROMPT' not in (root/'relay/usage.db').read_bytes()
            print('PASS: capture survives relay restart, commits then ACKs once, backfills a fresh dashboard after ACK, and never persists prompts')
        finally:
            relay.terminate()
            try:relay.wait(timeout=10)
            except subprocess.TimeoutExpired:
                relay.kill();relay.wait(timeout=10)
                raise AssertionError('relay did not stop gracefully')
            finally:server.shutdown();server.server_close()

if __name__=='__main__':main()
