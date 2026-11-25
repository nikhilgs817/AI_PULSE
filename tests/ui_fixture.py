"""Loopback-only Ollama protocol fixture for repeatable many-model UI checks.
No models are downloaded or executed. Fixture names identify synthetic data.
"""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=11436)
args = parser.parse_args()
models = [dict(name=f'fixture-model-{i:02d}:test', size=1073741824 * i / 2,
               size_vram=1073741824 * i / 4, context_length=8192,
               digest=f'fixture-{i}', details={'quantization_level': 'Q4_K_M'})
          for i in range(1, 13)]

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'  # keep-alive, as Ollama does; HTTP/1.0 closes break pooled clients
    def do_GET(self):
        if self.path not in ('/api/ps', '/api/tags'):
            self.send_error(404)
            return
        body = json.dumps({'models': models}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        # Deterministic /api/generate for the bench checks: right answers except the date case.
        if self.path != '/api/generate':
            self.send_error(404)
            return
        request = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))) or b'{}')
        prompt = request.get('prompt', '')
        answers = {'17 * 23': '391', 'Australia': 'Canberra', 'JSON': '{"ok": true}', 'primary colour': 'Red.', 'ISO 8601': '05/03/2026'}
        text = next((a for k, a in answers.items() if k in prompt), 'Air scatters short blue wavelengths more than red ones.')
        time.sleep(0.02)
        body = json.dumps({'model': request.get('model'), 'response': text, 'done': True,
                           'prompt_eval_count': len(prompt.split()), 'eval_count': len(text.split())}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass

print(f'12 synthetic Ollama models on loopback port {args.port}', flush=True)
ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
