"""Controlled live integration check. Requires a running AI Pulse gateway and existing models.
No models are downloaded. Keep-alive is five minutes; original loaded models are not unloaded.
"""
import argparse, concurrent.futures, json, pathlib, sqlite3, time, urllib.request, urllib.error

parser=argparse.ArgumentParser()
parser.add_argument('--database',required=True)
parser.add_argument('--output',required=True)
parser.add_argument('--gateway',default='http://127.0.0.1:11435')
parser.add_argument('--ollama',default='http://127.0.0.1:11434')
args=parser.parse_args()

def call(base,path,body=None):
    req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=240) as r:
        return r.read().decode()

results=[]
baseline=json.loads(call(args.ollama,'/api/ps'))
tags=json.loads(call(args.ollama,'/api/tags'))
names={m['name'] for m in tags['models']}
assert 'qwen3:8b' in names and 'nomic-embed-text:latest' in names,'Required existing small test models are missing; no download attempted.'
db=sqlite3.connect(args.database,timeout=15)
before={r[0] for r in db.execute("select id from usage where source='Ollama'")}
start=int(time.time())

def generate(stream=False):
    text=call(args.gateway,'/api/generate',{'model':'qwen3:8b','prompt':'Reply with a short sentence about the sky.','think':False,'stream':stream,'keep_alive':'5m','options':{'num_ctx':2048,'num_predict':48,'temperature':0}})
    return json.loads(text.splitlines()[-1]) if stream else json.loads(text)

first=generate()
results.append({'test':'native non-streaming request','passed':first.get('done') is True,'input':first.get('prompt_eval_count'),'output':first.get('eval_count'),'decode_tok_s':first['eval_count']/(first['eval_duration']/1e9) if first.get('eval_duration') else None})
second=generate(True)
results.append({'test':'native streaming request','passed':second.get('done') is True,'input':second.get('prompt_eval_count'),'output':second.get('eval_count')})
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    gen=pool.submit(generate)
    emb=pool.submit(call,args.gateway,'/api/embed',{'model':'nomic-embed-text:latest','input':'AI Pulse integration test.','keep_alive':'5m'})
    third=gen.result();embedding=json.loads(emb.result())
results.append({'test':'concurrent generation and embedding','passed':third.get('done') is True and bool(embedding.get('embeddings'))})
time.sleep(5)
loaded=json.loads(call(args.ollama,'/api/ps'))
results.append({'test':'multiple model residency','passed':len(loaded['models'])>=2,'loaded_models':[m['name'] for m in loaded['models']]})
try:
    call(args.gateway,'/api/generate',{'model':'ai-pulse-does-not-exist','prompt':'test','stream':False})
    results.append({'test':'upstream error preserved','passed':False})
except urllib.error.HTTPError as e:
    results.append({'test':'upstream error preserved','passed':e.code==404,'http_status':e.code})
time.sleep(2)
rows=[r for r in db.execute("select id,model,input,output,rate,status from usage where source='Ollama' and time>=?",(start,)) if r[0] not in before]
qwen=[r for r in rows if r[1]=='qwen3:8b' and r[5]=='Completed']
expected_input=sum(x.get('prompt_eval_count',0) for x in (first,second,third))
expected_output=sum(x.get('eval_count',0) for x in (first,second,third))
results.append({'test':'exact Qwen token accounting','passed':len(qwen)==3 and sum(r[2] for r in qwen)==expected_input and sum(r[3] for r in qwen)==expected_output,'requests':len(qwen),'expected_input':expected_input,'recorded_input':sum(r[2] for r in qwen),'expected_output':expected_output,'recorded_output':sum(r[3] for r in qwen)})
rates=sorted(r[4] for r in qwen if r[4] is not None)
expected_rates=sorted(x['eval_count']/(x['eval_duration']/1e9) for x in (first,second,third) if x.get('eval_duration'))
results.append({'test':'decode rate matches provider duration','passed':len(rates)==len(expected_rates) and all(abs(a-b)<1e-6 for a,b in zip(rates,expected_rates))})
report={'started_utc_epoch':start,'duration_seconds':round(time.time()-start,1),'baseline_loaded_models':[m['name'] for m in baseline['models']],'results':results,'all_passed':all(r['passed'] for r in results),'note':'Actual local Ollama traffic. No large-model load, model download, tuning, or forced unload. Test models expire after five minutes.'}
pathlib.Path(args.output).write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
raise SystemExit(0 if report['all_passed'] else 1)
