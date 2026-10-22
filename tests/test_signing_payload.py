import importlib.util
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location('signing_payload', Path(__file__).parents[1] / 'scripts/signing_payload.py')
payload = importlib.util.module_from_spec(spec)
spec.loader.exec_module(payload)


def pe(signed=False, pe64=True):
    data = bytearray(513)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 60, 128)
    data[128:132] = b'PE\0\0'
    struct.pack_into('<H', data, 148, 240 if pe64 else 224)
    struct.pack_into('<H', data, 152, 0x20b if pe64 else 0x10b)
    data[500:510] = b'owned-code'
    if signed:
        data.extend(bytes(7))
        struct.pack_into('<I', data, 216, 12345)
        struct.pack_into('<II', data, 152 + (112 if pe64 else 96) + 32, 520, 16)
        data.extend(b'certificate-data')
    return bytes(data)


class SigningPayloadTests(unittest.TestCase):
    def test_accepts_only_signature_changes_on_both_pe_architectures(self):
        for pe64 in (True, False):
            payload.verify_payload(pe(pe64=pe64), pe(True, pe64))

    def test_rejects_modified_executable_even_with_certificate(self):
        changed = bytearray(pe(True)); changed[502] ^= 1
        with self.assertRaisesRegex(ValueError, 'payload'):
            payload.verify_payload(pe(), bytes(changed))

    def test_rejects_unsigned_result(self):
        with self.assertRaises(ValueError): payload.verify_payload(pe(), pe())

    def test_rejects_unexpected_overlay_after_certificate(self):
        with self.assertRaises(ValueError): payload.verify_payload(pe(), pe(True) + b'extra')

    def test_rejects_truncated_header(self):
        with self.assertRaises(ValueError): payload.verify_payload(pe(), b'MZ')

    def test_rejects_certificate_table_pointing_at_program_data(self):
        changed = bytearray(pe(True)); struct.pack_into('<II', changed, 296, 128, len(changed) - 128)
        with self.assertRaises(ValueError): payload.verify_payload(pe(), bytes(changed))

    def test_rejects_nonzero_added_padding(self):
        changed = bytearray(pe(True)); changed[517] = 1
        with self.assertRaises(ValueError): payload.verify_payload(pe(), bytes(changed))


if __name__ == '__main__': unittest.main()
