#!/usr/bin/env python3
from pathlib import Path

def read(path):
    return Path(path).read_text(encoding="utf-8")

token = read("OpenSim/Framework/OfflineImMailRelayToken.cs")
for needle in (
    "HMACSHA256",
    "CryptographicOperations.FixedTimeEquals",
    "RandomNumberGenerator.GetBytes",
    "TryValidate(",
    "maximumFutureLifetime",
):
    assert needle in token, f"relay token helper missing {needle}"

offline = read("OpenSim/Addons/OfflineIM/OfflineIMRegionModule.cs")
for needle in (
    'RelaySigningKey',
    'ReplyLifetimeDays',
    'OfflineImMailRelayToken.Create(',
    'message.ReplyTo.Add',
    'BodyBuilder',
    'HtmlBody = htmlBody',
    '...Beyond the Reality',
    'X-NexVerse-IM-Reply-Enabled',
):
    assert needle in offline, f"offline IM email sender missing {needle}"

relay = read("NexVerse/Server/Api/NexOfflineImMailRelay.cs")
for needle in (
    "ImapClient",
    "SearchQuery.NotSeen",
    "relay@im.stadt-nexverse.de",
    "OfflineImMailRelayToken.TryValidate(",
    "GetUserAccount(UUID.Zero, senderEmail)",
    "replyingAccount.LocalToGrid",
    "IncomingInstantMessage(im)",
    "MessageFlags.Seen",
    "MaximumReplyCharacters",
):
    assert needle in relay, f"Robust IM mail relay missing {needle}"

connector = read("NexVerse/Server/Api/NexVerseWorldApiConnector.cs")
assert 'config.Configs["OfflineIMMailRelay"]' in connector
assert "new NexOfflineImMailRelay(" in connector
assert 'LoadOptionalService<IInstantMessage>(' in connector

hg = read("OpenSim/Services/HypergridService/HGInstantMessageService.cs")
assert "success = UndeliveredMessage(im);" in hg

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example"):
    text = read(path)
    section = text.split("[OfflineIMEmail]", 1)[1].split("\n[", 1)[0]
    assert 'RelayDomain = "im.stadt-nexverse.de"' in section
    assert 'RelaySigningKey = "${Environment|NEXVERSE_IM_RELAY_SIGNING_KEY}"' in section
    assert "ReplyLifetimeDays = 5" in section

for path in ("bin/Robust.HG.ini", "bin/Robust.HG.ini.example"):
    text = read(path)
    section = text.split("[OfflineIMMailRelay]", 1)[1].split("\n[", 1)[0]
    for needle in (
        "Enabled = true",
        'RelayDomain = "im.stadt-nexverse.de"',
        'RelaySigningKey = "${Environment|NEXVERSE_IM_RELAY_SIGNING_KEY}"',
        'IMAPUsername = "relay@im.stadt-nexverse.de"',
        'IMAPPassword = "${Environment|NEXVERSE_IM_RELAY_IMAP_PASSWORD}"',
        "MaximumReplyLifetimeDays = 5",
    ):
        assert needle in section, f"{path}: missing {needle}"

grid_common = read("bin/config-include/GridCommon.ini")
assert 'NEXVERSE_IM_RELAY_SIGNING_KEY = ""' in grid_common
assert 'NEXVERSE_IM_RELAY_IMAP_PASSWORD = ""' in grid_common

prebuild = read("prebuild.xml")
api = prebuild.split('<Project name="NexVerse.Server.Api"', 1)[1].split("</Project>", 1)[0]
assert '<Reference name="MailKit"/>' in api
assert '<Reference name="MimeKit"/>' in api

docs = read("doc/NexVerse/OFFLINE_IM_MAIL_RELAY.md")
assert "Catch-All" in docs
assert "openssl rand -hex 32" in docs

print("OpenGenesisLINK secure offline IM email reply relay: OK")
