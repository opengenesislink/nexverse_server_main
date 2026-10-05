#!/usr/bin/env python3
from pathlib import Path

api = Path("NexVerse/Server/Api/NexEconomyApi.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
viewer = Path("NexVerse/RegionModules/Economy/NexVerseMoneyModule.cs").read_text(encoding="utf-8")
openapi = Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")
opensim = Path("bin/OpenSim.ini.example").read_text(encoding="utf-8")

for marker in (
    "internal sealed class NexEconomyApi",
    '"/api/v1/economy/balance"',
    '"/api/v1/economy/virtual-account"',
    '"/api/v1/economy/transfers"',
    '"/api/v1/economy/transactions/"',
    '"reverse"',
    '"/api/v1/economy/accounts/"',
    "NexScopes.EconomyRead",
    "NexScopes.EconomyTransfer",
    "NexScopes.AdminAll",
    '"Idempotency-Key"',
    "DeterministicGuid(",
    "economy.Transfer(",
    "economy.Reverse(",
    "economy.SetAccountStatus(",
    "economy.EnsureResidentAccount(",
    "economy.EnsureVirtualBankAccount(",
    "NexVirtualBankAccount.Scheme",
):
    assert marker in api, f"missing economy API marker: {marker}"

for forbidden in (
    "INexLedgerStore",
    ".Append(",
    "NexLedgerSqlStore",
    "DbConnection",
    "AdministrativeAdjustment(",
):
    assert forbidden not in api, f"economy API bypasses policy boundary: {forbidden}"

for marker in (
    "new NexEconomyApi(",
    '"/api/v1/economy"',
    "NexEconomyConnector.Current?.Enabled == true",
    "NexEconomyConnector.Current.Economy",
):
    assert marker in connector, f"missing World API economy registration: {marker}"

for marker in (
    "public sealed class NexVerseMoneyModule",
    "IMoneyModule",
    "ISharedRegionModule",
    'Id = "NexVerseMoneyModule"',
    "scene.RegisterModuleInterface<IMoneyModule>",
    "OnEconomyDataRequest",
    "OnMoneyBalanceRequest",
    "OnMoneyTransfer",
    '"/api/v1/economy/balance?account_id="',
    '"/api/v1/economy/transfers"',
    '"X-NexVerse-Api-Key"',
    '"Idempotency-Key"',
    "SendMoneyBalance(",
    "SendEconomyData(",
):
    assert marker in viewer, f"missing Viewer economy adapter marker: {marker}"

for forbidden in (
    "DbConnection",
    "NexLedgerSqlStore",
    "INexLedgerStore",
    "ConnectionString",
    "MySql",
    "Npgsql",
    "Mono.Data.Sqlite",
):
    assert forbidden not in viewer, f"Viewer adapter must not access ledger DB directly: {forbidden}"

for marker in (
    '["/api/v1/economy/balance"]',
    '["/api/v1/economy/virtual-account"]',
    '["/api/v1/economy/transfers"]',
    '["/api/v1/economy/transactions/{transactionId}"]',
    '["/api/v1/economy/transactions/{transactionId}/reverse"]',
    '["/api/v1/economy/accounts/{accountId}/status"]',
    '["EconomyTransferRequest"]',
    '["EconomyBalanceResponse"]',
    '["EconomyVirtualAccountResponse"]',
    '["EconomyTransferResponse"]',
    '["EconomyTransactionResponse"]',
    '["EconomyAccountStatusRequest"]',
):
    assert marker in openapi, f"missing OpenAPI economy contract marker: {marker}"

assert "[NexEconomyViewer]" in opensim
viewer_config = opensim.split("[NexEconomyViewer]", 1)[1].split("\n[", 1)[0]
assert "Enabled = false" in viewer_config
assert 'ApiKey = "${Environment|NEXVERSE_ECONOMY_API_KEY}"' in viewer_config
assert "WorldApiBaseUrl" in viewer_config
assert "ConnectionString" not in viewer_config

print("NV$ World API and Viewer adapter contract: OK")
