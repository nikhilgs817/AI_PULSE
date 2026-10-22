"""Check that signing changed only the PE checksum, security directory and certificate."""
import argparse
import struct
from pathlib import Path


def pe_parts(data: bytes):
    if len(data) < 64 or data[:2] != b'MZ':
        raise ValueError('Not a PE file')
    header = struct.unpack_from('<I', data, 60)[0]
    if header + 24 > len(data) or data[header:header + 4] != b'PE\0\0':
        raise ValueError('Invalid PE header')
    optional = header + 24
    size = struct.unpack_from('<H', data, header + 20)[0]
    magic = struct.unpack_from('<H', data, optional)[0]
    directories = {0x10b: 96, 0x20b: 112}.get(magic)
    if directories is None or size < directories + 40 or optional + size > len(data):
        raise ValueError('Unsupported or truncated PE optional header')
    security = optional + directories + 32
    offset, length = struct.unpack_from('<II', data, security)
    if bool(offset) != bool(length):
        raise ValueError('Invalid certificate table')
    if length and (offset < optional + size or offset % 8 or offset + length != len(data)):
        raise ValueError('Certificate table must be aligned and at end of file')
    body = bytearray(data[:offset] if length else data)
    body[optional + 64:optional + 68] = bytes(4)
    body[security:security + 8] = bytes(8)
    return bytes(body), bool(length)


def verify_payload(original: bytes, signed: bytes):
    before, previously_signed = pe_parts(original)
    after, now_signed = pe_parts(signed)
    if previously_signed or not now_signed:
        raise ValueError('Expected an unsigned original and a signed result')
    padding = after[len(before):]
    if after[:len(before)] != before or len(padding) > 7 or any(padding):
        raise ValueError('Signing result changed executable payload')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('original', type=Path)
    parser.add_argument('signed', type=Path)
    args = parser.parse_args()
    verify_payload(args.original.read_bytes(), args.signed.read_bytes())
    print('PASS: signing preserved executable payload')
