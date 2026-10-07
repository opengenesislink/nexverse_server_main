#!/usr/bin/env python3
from pathlib import Path

def read(path):
    return Path(path).read_text(encoding="utf-8")

module = read("OpenSim/Addons/OfflineIM/OfflineIMRegionModule.cs")
for needle in (
    'im.dialog == (byte)InstantMessageDialog.MessageFromAgent',
    '!im.fromGroup',
    'm_OfflineIMService.StoreMessage(im, out reason)',
    'if (success &&',
    'account.LocalToGrid',
    'MailboxAddress.TryParse(recipient.Email.Trim()',
    'new MailboxAddress(m_EmailFromName, m_EmailFromAddress)',
    'SubjectTemplate',
    'ThreadPool.QueueUserWorkItem',
    'PerSenderPerHour',
    'PerRecipientPerHour',
    'GlobalPerHour',
):
    assert needle in module, f"OfflineIM email module missing {needle}"

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example"):
    text = read(path)
    assert 'OfflineMessageModule = "Offline Message Module V2"' in text
    section = text.split("[OfflineIMEmail]", 1)[1].split("\n[", 1)[0]
    for needle in (
        "Enabled = true",
        'RelayDomain = "im.stadt-nexverse.de"',
        'SubjectTemplate = "Offline-IM Nachricht von {SENDER}"',
        'SMTPPassword = "${Environment|NEXVERSE_OFFLINE_IM_SMTP_PASSWORD}"',
        "PerSenderPerHour = 30",
        "PerRecipientPerHour = 60",
        "GlobalPerHour = 500",
    ):
        assert needle in section, f"{path}: missing {needle}"

grid_common = read("bin/config-include/GridCommon.ini")
assert 'NEXVERSE_OFFLINE_IM_SMTP_PASSWORD = ""' in grid_common

prebuild = read("prebuild.xml")
offline_project = prebuild.split('<Project name="OpenSim.Addons.OfflineIM"', 1)[1].split("</Project>", 1)[0]
assert '<Reference name="MailKit"/>' in offline_project
assert '<Reference name="MimeKit"/>' in offline_project

secrets = read("doc/NexVerse/SECRETS.md")
assert "NEXVERSE_OFFLINE_IM_SMTP_PASSWORD" in secrets

print("OpenGenesisLINK offline IM email notification configuration: OK")
