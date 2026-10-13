#!/usr/bin/env python3
"""Decode a hex-dump capture of LiteNetLib 1.x traffic.

Input format: alternating timestamp lines and lowercase hex payload lines
(one UDP payload per line), which is what the in-game capture produces.

    ./tools/decode_lnl.py traffic.txt
"""
import re, sys, struct, collections

PROP = {0:'Unreliable',1:'Channeled',2:'Ack',3:'Ping',4:'Pong',5:'ConnectRequest',
        6:'ConnectAccept',7:'Disconnect',8:'UnconnectedMessage',9:'MtuCheck',10:'MtuOk',
        11:'Broadcast',12:'Merged',13:'ShutdownOk',14:'PeerNotFound',15:'InvalidProtocol',
        16:'NatMessage',17:'Empty'}

BATCH_MAGIC = 0x1160


def read_packed_uint(d, p):
    n = d[p] & 0x07
    if n == 5:
        return struct.unpack_from('<I', d, p + 1)[0], p + 5
    v = int.from_bytes(d[p:p + n], 'little')
    return v >> 3, p + n


def decode_ngo(payload):
    """Decode an NGO batch riding inside a Channeled/Unreliable LiteNetLib payload."""
    if len(payload) < 16:
        return "  (too short for a batch header)"
    magic, count, size = struct.unpack_from('<HHi', payload, 0)
    bhash = struct.unpack_from('<Q', payload, 8)[0]
    if magic != BATCH_MAGIC:
        return f"  not an NGO batch (magic 0x{magic:04x})"
    out = [f"  NGO batch: count={count} size={size} len={len(payload)} hash=0x{bhash:016x}"]
    p = 16
    for i in range(count):
        if p >= len(payload):
            break
        mtype, p = read_packed_uint(payload, p)
        msize, p = read_packed_uint(payload, p)
        out.append(f"    [{mtype}] {msize} bytes: {payload[p:p+min(msize,32)].hex()}")
        p += msize
    return "\n".join(out)


def main(path):
    lines = open(path).read().splitlines()
    events, ts = [], None
    for l in lines:
        l = l.strip()
        if re.match(r'^\d{4}-\d\d-\d\d', l):
            ts = l
        elif re.fullmatch(r'[0-9a-f]+', l) and len(l) >= 2:
            events.append((ts, bytes.fromhex(l)))

    hist = collections.Counter()
    for ts, b in events:
        prop = b[0] & 0x1f
        name = PROP.get(prop, f'?{prop}')
        hist[name] += 1
        t = ts.split()[1][:12] if ts else ''
        detail = ''
        if name == 'ConnectRequest':
            pid = struct.unpack_from('<i', b, 1)[0]
            ct = struct.unpack_from('<q', b, 5)[0]
            asz = b[17]
            addr = b[18:18 + asz]
            port = struct.unpack_from('>H', addr, 2)[0]
            ip = '.'.join(str(x) for x in addr[4:8])
            detail = (f"protocolId={pid} ct={ct} target={ip}:{port} "
                      f"connectData={b[18+asz:].hex()}")
        elif name == 'ConnectAccept':
            # [1..8] connectionTime, [9] connectionNumber, [10] isReused, [11..14] peerId
            detail = (f"ct={struct.unpack_from('<q', b, 1)[0]} "
                      f"connNum={b[9]} reused={b[10]} "
                      f"peerId={struct.unpack_from('<i', b, 11)[0]}")
        elif name in ('Ping', 'Pong'):
            detail = f"seq={struct.unpack_from('<H', b, 1)[0]}"
        elif name == 'MtuCheck':
            detail = f"mtu={struct.unpack_from('<i', b, 1)[0]}"
        print(f"{t:13} {name:16} {len(b):5}  {detail}")
        if name in ('Channeled', 'Unreliable'):
            # Channeled carries a 4-byte LNL header, Unreliable a 1-byte one.
            print(decode_ngo(b[4:] if name == 'Channeled' else b[1:]))

    print("\nhistogram:")
    for k, v in hist.most_common():
        print(f"  {k:16} {v}")
    if not hist['Channeled'] and not hist['Unreliable']:
        print("\n  NOTE: no data packets at all - the NGO layer never got a chance to speak.")
    if hist['Ping'] and not hist['Pong']:
        print("  NOTE: pings with no pongs. These captures carry no direction or port info, so"
              "\n        note the pings are most likely the SERVER's: it accepted the connection"
              "\n        and started pinging, while the client is still in Outgoing state and so"
              "\n        never pongs. Expect a DisconnectTimeout (~5s) loop on the server.")
    if hist['ConnectRequest'] > 3 and hist['ConnectAccept']:
        print("  NOTE: repeated ConnectRequests answered by ConnectAccepts. If the connectionTime"
              "\n        is identical across them it is ONE attempt being resent - the client is"
              "\n        not acting on the accept. Capture with tcpdump to see if it arrives.")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'traffic.txt')
