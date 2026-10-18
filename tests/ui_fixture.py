"""Loopback-only Ollama protocol fixture for repeatable many-model UI checks.
No models are downloaded or executed. Fixture names identify synthetic data.
"""
import argparse
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=11436)
args = parser.parse_args()
models = [dict(name=f'fixture-model-{i:02d}:test', size=1073741824 * i / 2,
               size_vram=1073741824 * i / 4, context_length=8192,
               digest=f'fixture-{i}', details={'quantization_level': 'Q4_K_M'})
          for i in range(1, 13)]

class Handler(BaseHTTPRequestHandler):
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

    def log_message(self, *_):
        pass

print(f'12 synthetic Ollama models on loopback port {args.port}', flush=True)
HTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
