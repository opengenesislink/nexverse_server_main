#!/usr/bin/env python3
from pathlib import Path
crypto=Path("NexVerse/RegionModules/Archives/OglEncryptedBackup.cs").read_text()
ops=Path("NexVerse/RegionModules/Archives/OglIarOperationManager.cs").read_text()
required=[
(crypto,'"OGLBAK01"'),(crypto,"AesGcm"),(crypto,"Rfc2898DeriveBytes.Pbkdf2"),
(crypto,"HashAlgorithmName.SHA256"),(crypto,"RandomNumberGenerator.GetBytes"),
(crypto,"CryptographicOperations.ZeroMemory"),(crypto,"AuthenticationTagMismatchException"),
(crypto,"File.Move(temp, backupPath, true)"),(ops,"CreateEncryptedBackup"),
(ops,"RestoreEncryptedBackup"),(ops,'EndsWith(".oglbackup"'),(ops,"OglIarInspector.Inspect(destination)")
]
missing=[n for t,n in required if n not in t]
if missing: raise SystemExit("Encrypted IAR backup missing: "+", ".join(missing))
for banned in ["DES","TripleDES","Aes.Create()","PasswordDeriveBytes"]:
    if banned in crypto: raise SystemExit("Disallowed backup cryptography: "+banned)
if "passphrase" in Path("NexVerse/Server/Api/OglIarApi.cs").read_text():
    raise SystemExit("Backup passphrases must not be added to World API payloads")
print("OpenGenesisLINK encrypted IAR backup: OK")
