# Проверка Lua-скриптов на живом Redis (ВНИМАНИЕ: делает FLUSHALL, только для тестового Redis):
#   pip install redis && REDIS_PORT=6379 python scripts/check_lua.py
import re, redis, threading, random
src=open(__import__('os').path.join(__import__('os').path.dirname(__file__), '..', 'src')+'/ExecutorBalancer.Infrastructure/Redis/RedisLoadStore.cs').read()
def script(name):
    m=re.search(name+r' = """\n(.*?)\n\s*""";',src,re.S); return "\n".join(l[8:] for l in m.group(1).split("\n"))
PICK,REL=script('PickScript'),script('ReleaseScript')
r=redis.Redis(port=int(__import__('os').environ.get('REDIS_PORT', '6379')),decode_responses=True); r.flushall()
pick=r.register_script(PICK); rel=r.register_script(REL)
K=lambda oid,day="20260928":["{eb}:open-weight","{eb}:open-count","{eb}:daily:"+day,"{eb}:active","{eb}:order:%d"%oid]
def P(oid,w,cands,reopen=0,rr=r):
    args=[w,reopen,259200]
    for c in cands: args+=list(c)
    return (rr if rr is r else rr).evalsha(pick.sha,5,*K(oid),*args) if False else pick(keys=K(oid),args=args,client=rr)
def R(oid,state,delete=0): return rel(keys=["{eb}:open-weight","{eb}:open-count","{eb}:order:%d"%oid],args=[state,604800,delete])
for i in (1,2,3): r.hset("{eb}:active",i,1)
# weighted [2,1,2]
res=[P(i,1000,[(1,1000,-1),(2,2000,-1)])[1] for i in (1,2,3)]; print("weighted",res); assert res==['2','1','2']
# duplicate
d=P(1,1000,[(1,1000,-1),(2,2000,-1)]); print("dup",d); assert d[0]=='existing' and d[1]=='2'
# tie -> lowest id
r.flushall(); [r.hset("{eb}:active",i,1) for i in (3,7)]
t=P(1,1000,[(3,1000,-1),(7,1000,-1)]); assert t[1]=='3', t
# inactive + limit
r.hset("{eb}:active",7,0)
t=P(2,1000,[(3,1000,1),(7,1000,-1)]); print("none",t); assert t[0]=='none' and '3|daily_limit_exceeded|1000|1' in t and '7|inactive|0|0' in t
# forced ignores limit (limit -1 passed)
t=P(2,1000,[(3,1000,-1)]); assert t[0]=='assigned' and t[1]=='3'
# release await, reopen
assert R(2,'await')==1 and R(2,'await')==0
assert P(2,1000,[(3,1000,-1)])[0]=='existing'           # без reopen не переназначаем
t=P(2,1000,[(3,1000,-1)],reopen=1); assert t[0]=='assigned', t
assert P(2,1000,[(3,1000,-1)],reopen=1)[0]=='existing'   # уже открыта
assert R(2,'closed')==1 and r.ttl("{eb}:order:2")>0
# rollback deletes
t=P(9,1500,[(3,1000,-1)]); assert R(9,'rolled-back',1)==1 and not r.exists("{eb}:order:9")
print("loads",r.hgetall("{eb}:open-weight"),r.hgetall("{eb}:open-count"))
assert r.hget("{eb}:open-weight",3)=='1000' and r.hget("{eb}:open-count",3)=='1'
# concurrency: 8 clients, 4000 orders sent twice, limit 300 on exec 1
r.flushall(); ids=list(range(1,11)); [r.hset("{eb}:active",i,1) for i in ids]
cands=[(i,2000 if i%3==0 else 1000,300 if i==1 else -1) for i in ids]
out=[];lock=threading.Lock()
orders=list(range(1,4001))*2; random.shuffle(orders)
def worker(chunk):
    c=redis.Redis(port=int(__import__('os').environ.get('REDIS_PORT', '6379')),decode_responses=True); loc=[]
    for o in chunk: loc.append((o,P(o,1000,cands,rr=c)))
    with lock: out.extend(loc)
th=[threading.Thread(target=worker,args=(orders[k::8],)) for k in range(8)]
[t.start() for t in th];[t.join() for t in th]
assigned=[x for x in out if x[1][0]=='assigned']; print("assigned",len(assigned),"existing",sum(x[1][0]=='existing' for x in out))
assert len(assigned)==4000 and len({o for o,_ in assigned})==4000
cnt={int(k):int(v) for k,v in r.hgetall("{eb}:open-count").items()}; print(cnt)
assert sum(cnt.values())==4000 and int(r.hget("{eb}:daily:20260928",1))<=300
per=[int(r.hget("{eb}:open-weight",i))/q for i,q,_ in cands if i!=1]; print("spread per qual",max(per)-min(per))
assert max(per)-min(per)<=1
print("LUA OK")
