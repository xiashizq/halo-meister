import struct
d = open('decoded_payload.bin','rb').read()

class R:
    def __init__(s,b,p=0): s.b,s.p=b,p
    def i32(s):
        v=struct.unpack_from('<i',s.b,s.p)[0]; s.p+=4; return v
    def u8(s):
        v=s.b[s.p]; s.p+=1; return v
    def fstr(s):
        n=s.i32()
        if n==0: return ''
        raw=s.b[s.p:s.p+n]; s.p+=n
        return raw[:-1].decode('utf-8','replace')

TP={'StructProperty':2,'ArrayProperty':1,'SetProperty':1,'MapProperty':2}

def parse(buf):
    r=R(buf); props=[]; term=False
    while r.p < len(buf):
        name=r.fstr()
        if name=='None': term=True; break
        typ=r.fstr()
        params=[(r.i32(), r.fstr()) for _ in range(TP.get(typ,0))]
        idx=r.i32(); size=r.i32(); flags=r.u8()
        aidx = r.i32() if (flags & 1) else None
        body=buf[r.p:r.p+size]; r.p+=size
        props.append({'name':name,'type':typ,'params':params,'idx':idx,'flags':flags,'aidx':aidx,'body':body})
    return props, term, buf[r.p:]

def write(props, term, trailer=b''):
    o=bytearray()
    def fs(s):
        b=s.encode('utf-8')+b'\x00'; o.extend(struct.pack('<i',len(b))); o.extend(b)
    for p in props:
        fs(p['name']); fs(p['type'])
        for c,s in p['params']: o.extend(struct.pack('<i',c)); fs(s)
        o.extend(struct.pack('<i',p['idx'])); o.extend(struct.pack('<i',len(p['body'])))
        o.append(p['flags'])
        if p['aidx'] is not None: o.extend(struct.pack('<i',p['aidx']))
        o.extend(p['body'])
    if term: fs('None')
    o.extend(trailer)
    return bytes(o)

def dump(props, d=0):
    for p in props:
        ai='' if p['aidx'] is None else f'[{p["aidx"]}]'
        st = p['params'][0][1] if p['params'] else None
        print('  '*d+f"{p['name']}{ai} : {p['type']}{'<'+st+'>' if st else ''} flags={p['flags']:#04x} size={len(p['body'])}")
        if p['type']=='StructProperty' and st!='GameplayTagContainer':
            sub,t,rest = parse(p['body']); dump(sub, d+1)
            assert not rest, ('rest', rest[:20])
        elif p['type']=='StructProperty':
            r=R(p['body']); n=r.i32(); tags=[r.fstr() for _ in range(n)]
            print('  '*(d+1)+f"<{n} tags, consumed {r.p}/{len(p['body'])}>")
        elif p['type']=='ArrayProperty':
            r=R(p['body']); n=r.i32(); items=[r.fstr() for _ in range(n)]
            print('  '*(d+1)+f"{items} consumed {r.p}/{len(p['body'])}")
        elif p['type']=='IntProperty':
            print('  '*(d+1)+f"= {struct.unpack('<i',p['body'])[0]}")
        elif p['type']=='BoolProperty':
            print('  '*(d+1)+f"= {(p['flags'] & 0x10)!=0}")

props, term, trailer = parse(d[1:])
dump(props)
print('trailer:', trailer.hex(), 'terminated:', term)
rt = bytes([d[0]]) + write(props, term, trailer)
print('ROUND TRIP EXACT:', rt == d, len(rt), '==', len(d))
