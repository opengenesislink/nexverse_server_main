#!/usr/bin/env python3
"""Unit tests for configuration-only, secret-free deployment diagnostics."""
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "preflight", Path(__file__).with_name("ogl_voice_pathfinding_preflight.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PreflightTests(unittest.TestCase):
    def test_read_ini_ignores_comments_and_keeps_effective_last_value(self):
        cfg = module.parse_opensim_ini("""
; [OGLVoice] Enabled = true
[OGLVoice]
Enabled = false
Enabled = true ; override
MediaGatewayUrl = "https://media.example.org/internal/webrtc/v1/exchange"
SharedKey = "must-not-print"
""")
        self.assertTrue(module.enabled(cfg, "OGLVoice"))
        self.assertEqual(module.setting(cfg, "OGLVoice", "MediaGatewayUrl"),
                         "https://media.example.org/internal/webrtc/v1/exchange")
        self.assertNotIn("must-not-print", str(module.assess({}, cfg)))

    def test_no_false_navmesh_success(self):
        sim = module.parse_opensim_ini("[OGLPathfinding]\nEnabled = true\n")
        issues = module.assess(sim, viewer_log="cannot find capability 'RetrieveNavMeshSrc'")
        codes = {x["code"] for x in issues}
        self.assertIn("NATIVE_TERRAIN_ENABLED", codes)
        self.assertIn("FIRESTORM_NAVMESH_CAP_MISSING", codes)

    def test_missing_gateway_and_old_vivox(self):
        sim = module.parse_opensim_ini("[NexVerseNodeAgent]\nEnabled = true\n")
        robust = module.parse_opensim_ini(
            "[OGLVoice]\nEnabled = true\nEnableFirestormGateway = false\nMediaGatewayUrl = \"\"")
        issues = module.assess(sim, robust, "cannot POST url ''\nUnable to provision voice account.")
        codes = {x["code"] for x in issues}
        self.assertIn("ROBUST_FIRESTORM_GATEWAY_DISABLED", codes)
        self.assertIn("ROBUST_GATEWAY_URL_INVALID", codes)
        self.assertIn("FIRESTORM_VIVOX_FALLBACK", codes)

    def test_configured_not_claimed_functional(self):
        sim = module.parse_opensim_ini(
            "[NexVerseNodeAgent]\nEnabled = true\n[OGLPathfinding]\nEnabled = true")
        robust = module.parse_opensim_ini("""
[OGLVoice]
Enabled = true
EnableFirestormGateway = true
MediaGatewayUrl = "https://media.example.net/internal/webrtc/v1/exchange"
""")
        issues = module.assess(sim, robust)
        code = {x["code"]: x for x in issues}
        self.assertEqual(code["ROBUST_GATEWAY_CONFIGURED"]["severity"], "configuration-only")
        self.assertNotIn("success", [x["severity"] for x in issues])

    def test_media_url_avoids_unsafe_targets(self):
        for value in (
            "https://voice.example.org", "wss://livekit.example.org",
            "http://media.example.org/internal/webrtc/v1/exchange",
            "https://name:secret@media.example.org/internal/webrtc/v1/exchange",
            "https://VOICE-MEDIA-BRIDGE-HOST/internal/webrtc/v1/exchange",
            "https://example.org/internal/webrtc/v1/exchange#frag"
        ):
            self.assertFalse(module.media_url_valid(value), value)
        self.assertTrue(module.media_url_valid(
            "https://media.example.org/internal/webrtc/v1/exchange"))
        self.assertTrue(module.media_url_valid(
            "http://127.0.0.1:19098/internal/webrtc/v1/exchange"))

    def test_standalone_requires_key_in_process_environment(self):
        sim = module.parse_opensim_ini("""
[OGLVoice]
Enabled = true
Mode = "Standalone"
TenantId = "nexverse"
EnableFirestormGateway = true
MediaGatewayUrl = "https://media.example.org/internal/webrtc/v1/exchange"
""")
        codes = {x["code"] for x in module.assess(sim)}
        self.assertIn("STANDALONE_GATEWAY_CONFIGURED", codes)
        self.assertIn("STANDALONE_SIGNATURE_KEY_UNVERIFIED", codes)


if __name__ == "__main__":
    unittest.main()
