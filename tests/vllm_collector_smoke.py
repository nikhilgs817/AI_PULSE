"""Real Collector Prometheus->OTLP->AIPULSE integration with synthetic vLLM counters.
Does not start a model or require a Kubernetes cluster.
"""
import argparse, contextlib, http.server, json, pathlib, socket, sqlite3, subprocess, tempfile, threading, time, urllib.request

def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]

class Metrics(http.server.BaseHTTPRequestHandler):
    samples=0
    def log_message(self,*_):pass
    def do_GET(self):
        Metrics.samples+=1;n=Metrics.samples
        text=f'''# TYPE vllm:prompt_tokens_total counter
vllm:prompt_tokens_total{{model_name="fixture",engine="0"}} {1000+n*50}
# TYPE vllm:generation_tokens_total counter
vllm:generation_tokens_total{{model_name="fixture",engine="0"}} {500+n*10}
'''
        self.send_response(200);self.send_header('Content-Type','text/plain; version=0.0.4');self.end_headers();self.wfile.write(text.encode())

def main():
    parser=argparse.ArgumentParser();parser.add_argument('app');parser.add_argument('collector');args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='aipulse-vllm-test-') as temp:
        root=pathlib.Path(temp);scrape,receiver=port(),port()
        server=http.server.ThreadingHTTPServer(('127.0.0.1',scrape),Metrics)
        threading.Thread(target=server.serve_forever,daemon=True).start()
        config=pathlib.Path('docs/telemetry/otelcol-vllm-local.yaml').read_text().replace('127.0.0.1:8000',f'127.0.0.1:{scrape}').replace('127.0.0.1:4319',f'127.0.0.1:{receiver}').replace('scrape_interval: 15s','scrape_interval: 1s')
        (root/'collector.yaml').write_text(config)
        (root/'settings.json').write_text(json.dumps(dict(ReadAgentUsage=False,EnableGateway=False,TelemetryPort=receiver,LastBackup='2099-01-01T00:00:00Z')))
        app=subprocess.Popen([args.app,'--collect','--seconds','30','--data-dir',temp],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);collector=None
        try:
            for _ in range(100):
                try:
                    with urllib.request.urlopen(f'http://127.0.0.1:{receiver}/health',timeout=1) as r:
                        if json.load(r)['service']=='AIPULSE OTLP/HTTP':break
                except OSError:pass
                time.sleep(.1)
            with (root/'collector-errors.txt').open('w') as errors:
                collector=subprocess.Popen([args.collector,'--config',str(root/'collector.yaml')],stdout=subprocess.DEVNULL,stderr=errors)
                observed=None
                for _ in range(120):
                    time.sleep(.1)
                    with contextlib.closing(sqlite3.connect(root/'data/pulse.db')) as db:
                        observed=db.execute("select kind,sum(tokens) from token_intervals where source='vLLM counters' group by kind").fetchall()
                        requests=db.execute('select count(*) from usage').fetchone()[0]
                    if dict(observed).get('input',0)>=100 and dict(observed).get('output',0)>=20:break
                assert dict(observed).get('input',0)>=100 and dict(observed).get('output',0)>=20,(observed,(root/'collector-errors.txt').read_text())
                assert dict(observed)['input']==dict(observed)['output']*5
                assert requests==0,'Server counters must not create request records'
                print('PASS: real OpenTelemetry Collector scrapes vLLM-compatible counters, exports protobuf, preserves counter ratio and baseline semantics:',dict(observed))
        finally:
            if collector:collector.terminate();collector.wait(timeout=10)
            app.terminate();app.wait(timeout=10);server.shutdown();server.server_close()

if __name__=='__main__':main()
