// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Economy;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexEconomyApi
    {
        private const int IdempotencyKeyLimit = 128;
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IUserAccountService m_UserAccounts;
        private readonly INexAuditSink m_Audit;
        private readonly Func<NexEconomyService> m_EconomyProvider;

        public NexEconomyApi(
            NexApiAuthenticator authenticator,
            IUserAccountService userAccounts,
            INexAuditSink audit,
            Func<NexEconomyService> economyProvider)
        {
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_UserAccounts =
                userAccounts ??
                throw new ArgumentNullException(nameof(userAccounts));
            m_Audit =
                audit ??
                NullNexAuditSink.Instance;
            m_EconomyProvider =
                economyProvider ??
                throw new ArgumentNullException(nameof(economyProvider));
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');
            string method =
                request?.HttpMethod ??
                string.Empty;

            if (string.Equals(
                    path,
                    "/api/v1/economy/balance",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    MethodNotAllowed(response, "GET");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EconomyRead,
                        out NexPrincipal principal,
                        out UserAccount account))
                {
                    return;
                }

                HandleBalance(
                    request,
                    response,
                    principal,
                    account);
                return;
            }

            if (string.Equals(
                    path,
                    "/api/v1/economy/virtual-account",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    MethodNotAllowed(response, "GET");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EconomyRead,
                        out NexPrincipal principal,
                        out UserAccount account))
                {
                    return;
                }

                HandleVirtualAccount(
                    request,
                    response,
                    principal,
                    account);
                return;
            }

            if (string.Equals(
                    path,
                    "/api/v1/economy/transfers",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    MethodNotAllowed(response, "POST");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EconomyTransfer,
                        out NexPrincipal principal,
                        out UserAccount account))
                {
                    return;
                }

                HandleTransfer(
                    request,
                    response,
                    principal,
                    account);
                return;
            }

            if (string.Equals(
                    path,
                    "/api/v1/economy/accounts/ensure",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    MethodNotAllowed(response, "POST");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EconomyTransfer,
                        out NexPrincipal principal,
                        out UserAccount account))
                {
                    return;
                }

                if (account != null &&
                    !principal.HasScope(NexScopes.AdminAll))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Forbidden,
                        "economy_wallet_provision_forbidden",
                        "Wallet provisioning is restricted to trusted services and administrators.");
                    return;
                }

                HandleEnsureWallet(
                    request,
                    response,
                    principal);
                return;
            }

            const string transactionPrefix =
                "/api/v1/economy/transactions/";

            if (path.StartsWith(
                    transactionPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string relative =
                    path.Substring(
                        transactionPrefix.Length);
                string[] parts =
                    relative.Split(
                        '/',
                        StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0 ||
                    !Guid.TryParse(
                        parts[0],
                        out Guid transactionId) ||
                    transactionId == Guid.Empty)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_transaction_id",
                        "A valid non-zero transaction UUID is required.");
                    return;
                }

                if (parts.Length == 1)
                {
                    if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        MethodNotAllowed(response, "GET");
                        return;
                    }

                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.EconomyRead,
                            out NexPrincipal principal,
                            out UserAccount account))
                    {
                        return;
                    }

                    HandleTransaction(
                        response,
                        transactionId,
                        principal,
                        account);
                    return;
                }

                if (parts.Length == 2 &&
                    string.Equals(
                        parts[1],
                        "reverse",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        MethodNotAllowed(response, "POST");
                        return;
                    }

                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.AdminAll,
                            out NexPrincipal principal,
                            out UserAccount _))
                    {
                        return;
                    }

                    HandleReverse(
                        request,
                        response,
                        transactionId,
                        principal);
                    return;
                }

                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "not_found",
                    "Unknown NV$ transaction endpoint.");
                return;
            }

            const string accountPrefix =
                "/api/v1/economy/accounts/";

            if (path.StartsWith(
                    accountPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string relative =
                    path.Substring(
                        accountPrefix.Length);
                string[] parts =
                    relative.Split(
                        '/',
                        StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2 &&
                    Guid.TryParse(
                        parts[0],
                        out Guid accountId) &&
                    accountId != Guid.Empty &&
                    string.Equals(
                        parts[1],
                        "status",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        MethodNotAllowed(response, "POST");
                        return;
                    }

                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.AdminAll,
                            out NexPrincipal principal,
                            out UserAccount _))
                    {
                        return;
                    }

                    HandleAccountStatus(
                        request,
                        response,
                        accountId,
                        principal);
                    return;
                }

                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "not_found",
                    "Unknown NV$ account endpoint.");
                return;
            }

            WriteError(
                response,
                HttpStatusCode.NotFound,
                "not_found",
                "Unknown NV$ economy endpoint.");
        }

        private void HandleBalance(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticatedAccount)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            if (!TryResolveReadAccount(
                    request,
                    response,
                    principal,
                    authenticatedAccount,
                    out Guid accountId))
            {
                return;
            }

            try
            {
                NexLedgerAccount account =
                    EnsureAccountIfResident(
                        economy,
                        accountId);

                if (account == null)
                {
                    WriteError(
                        response,
                        HttpStatusCode.NotFound,
                        "economy_account_not_found",
                        "The NV$ account was not found.");
                    return;
                }

                NexLedgerAccountState state =
                    economy.GetAccountState(
                        accountId);

                WriteJson(
                    response,
                    new
                    {
                        account =
                            AccountPayload(
                                account,
                                economy.GetBalance(accountId),
                                state),
                        currency = CurrencyPayload(),
                        correlation_id = Correlation(response)
                    });
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleVirtualAccount(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticatedAccount)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            if (!TryResolveReadAccount(
                    request,
                    response,
                    principal,
                    authenticatedAccount,
                    out Guid accountId))
            {
                return;
            }

            try
            {
                NexLedgerAccount account =
                    EnsureAccountIfResident(
                        economy,
                        accountId);

                if (account == null)
                {
                    WriteError(
                        response,
                        HttpStatusCode.NotFound,
                        "economy_account_not_found",
                        "The NV$ account was not found.");
                    return;
                }

                NexVirtualBankAccount virtualAccount =
                    economy.EnsureVirtualBankAccount(
                        account.AccountId);

                WriteJson(
                    response,
                    new
                    {
                        virtual_account =
                            VirtualAccountPayload(
                                virtualAccount),
                        currency =
                            CurrencyPayload(),
                        correlation_id =
                            Correlation(response)
                    });
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleTransfer(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticatedAccount)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            string idempotencyKey =
                (request?.Headers?["Idempotency-Key"] ??
                 string.Empty)
                    .Trim();

            if (idempotencyKey.Length == 0 ||
                idempotencyKey.Length > IdempotencyKeyLimit)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_idempotency_key",
                    "Idempotency-Key is required and may contain at most 128 characters.");
                return;
            }

            if (!TryBody(
                    request,
                    response,
                    out JsonElement body))
            {
                return;
            }

            if (!TryGuid(
                    body,
                    "to_account_id",
                    out Guid destinationId) ||
                !TryPositiveInt64(
                    body,
                    "amount",
                    out long amount))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_transfer_request",
                    "to_account_id and a positive integer amount are required.");
                return;
            }

            string reference =
                GetOptionalString(
                    body,
                    "reference");

            if (string.IsNullOrWhiteSpace(reference))
                reference = "NV$ transfer";

            Guid sourceId;

            if (authenticatedAccount != null)
            {
                sourceId =
                    authenticatedAccount.PrincipalID.Guid;

                if (body.TryGetProperty(
                        "from_account_id",
                        out JsonElement sourceElement) &&
                    sourceElement.ValueKind != JsonValueKind.Null)
                {
                    if (!Guid.TryParse(
                            sourceElement.GetString(),
                            out Guid requestedSource) ||
                        requestedSource != sourceId)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.Forbidden,
                            "economy_source_forbidden",
                            "Resident tokens may transfer only from their own NV$ account.");
                        return;
                    }
                }
            }
            else
            {
                if (!TryGuid(
                        body,
                        "from_account_id",
                        out sourceId))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "source_account_required",
                        "Service/API-key transfers require from_account_id.");
                    return;
                }
            }

            try
            {
                EnsureAccountIfResident(
                    economy,
                    sourceId);
                EnsureAccountIfResident(
                    economy,
                    destinationId);

                Guid transactionId =
                    DeterministicGuid(
                        "economy-transfer:" +
                        principal.Subject +
                        ":" +
                        idempotencyKey);

                string correlationId =
                    Correlation(
                        response);

                NexLedgerAppendResult result =
                    economy.Transfer(
                        sourceId,
                        destinationId,
                        amount,
                        reference,
                        correlationId,
                        transactionId);

                m_Audit.Record(
                    new NexAuditEvent(
                        principal.Subject,
                        result.Created
                            ? "economy.transfer.created"
                            : "economy.transfer.duplicate",
                        "ledger-transaction:" +
                        transactionId.ToString("D"),
                        correlationId,
                        new Dictionary<string, string>
                        {
                            ["from_account_id"] =
                                sourceId.ToString("D"),
                            ["to_account_id"] =
                                destinationId.ToString("D"),
                            ["amount"] =
                                amount.ToString()
                        }));

                WriteJson(
                    response,
                    new
                    {
                        status =
                            result.Created
                                ? "created"
                                : "duplicate",
                        transaction =
                            TransactionPayload(
                                result.Transaction),
                        balances = new
                        {
                            source =
                                economy.GetBalance(
                                    sourceId),
                            destination =
                                economy.GetBalance(
                                    destinationId)
                        },
                        currency =
                            CurrencyPayload(),
                        correlation_id =
                            correlationId
                    },
                    result.Created
                        ? HttpStatusCode.Created
                        : HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleEnsureWallet(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            if (!TryBody(
                    request,
                    response,
                    out JsonElement body))
            {
                return;
            }

            if (!TryGuid(
                    body,
                    "account_id",
                    out Guid accountId))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "account_id_required",
                    "account_id is required.");
                return;
            }

            string rawClass =
                GetOptionalString(
                    body,
                    "account_class")
                    .Replace("-", string.Empty)
                    .Replace("_", string.Empty);

            NexLedgerAccountClass accountClass;

            if (rawClass.Equals("group", StringComparison.OrdinalIgnoreCase))
                accountClass = NexLedgerAccountClass.Group;
            else if (rawClass.Equals("business", StringComparison.OrdinalIgnoreCase) ||
                     rawClass.Equals("merchant", StringComparison.OrdinalIgnoreCase))
                accountClass = NexLedgerAccountClass.Business;
            else if (rawClass.Equals("estate", StringComparison.OrdinalIgnoreCase))
                accountClass = NexLedgerAccountClass.Estate;
            else if (rawClass.Equals("objectmerchantendpoint", StringComparison.OrdinalIgnoreCase) ||
                     rawClass.Equals("object", StringComparison.OrdinalIgnoreCase))
                accountClass = NexLedgerAccountClass.ObjectMerchantEndpoint;
            else
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_account_class",
                    "account_class must be group, business, estate or object_merchant_endpoint.");
                return;
            }

            try
            {
                NexLedgerAccount account =
                    economy.EnsureWalletAccount(
                        accountId,
                        accountClass,
                        GetOptionalString(
                            body,
                            "display_name"));

                NexLedgerAccountState state =
                    economy.GetAccountState(
                        account.AccountId);

                m_Audit.Record(
                    new NexAuditEvent(
                        principal.Subject,
                        "economy.wallet.ensure",
                        "ledger-account:" +
                        account.AccountId.ToString("D"),
                        Correlation(response),
                        new Dictionary<string, string>
                        {
                            ["account_class"] =
                                account.AccountClass.ToString()
                        }));

                WriteJson(
                    response,
                    new
                    {
                        account =
                            AccountPayload(
                                account,
                                economy.GetBalance(account.AccountId),
                                state),
                        correlation_id =
                            Correlation(response)
                    },
                    HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleTransaction(
            IOSHttpResponse response,
            Guid transactionId,
            NexPrincipal principal,
            UserAccount authenticatedAccount)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            try
            {
                NexLedgerTransaction transaction =
                    economy.GetTransaction(
                        transactionId);

                if (transaction == null)
                {
                    WriteError(
                        response,
                        HttpStatusCode.NotFound,
                        "economy_transaction_not_found",
                        "The NV$ transaction was not found.");
                    return;
                }

                if (authenticatedAccount != null &&
                    !principal.HasScope(
                        NexScopes.AdminAll) &&
                    !transaction.Postings.Any(
                        x => x.AccountId ==
                             authenticatedAccount.PrincipalID.Guid))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Forbidden,
                        "economy_transaction_forbidden",
                        "Resident tokens may read only transactions involving their own NV$ account.");
                    return;
                }

                WriteJson(
                    response,
                    new
                    {
                        transaction =
                            TransactionPayload(
                                transaction),
                        currency =
                            CurrencyPayload(),
                        correlation_id =
                            Correlation(response)
                    });
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleReverse(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid transactionId,
            NexPrincipal principal)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            if (!TryBody(
                    request,
                    response,
                    out JsonElement body))
            {
                return;
            }

            string reason =
                GetOptionalString(
                    body,
                    "reason");

            if (string.IsNullOrWhiteSpace(reason))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "reversal_reason_required",
                    "reason is required.");
                return;
            }

            try
            {
                string correlationId =
                    Correlation(response);

                NexLedgerAppendResult result =
                    economy.Reverse(
                        transactionId,
                        principal.Subject,
                        reason);

                m_Audit.Record(
                    new NexAuditEvent(
                        principal.Subject,
                        result.Created
                            ? "economy.transaction.reversed"
                            : "economy.transaction.reversal_duplicate",
                        "ledger-transaction:" +
                        transactionId.ToString("D"),
                        correlationId,
                        new Dictionary<string, string>
                        {
                            ["reversal_transaction_id"] =
                                result.Transaction.TransactionId.ToString("D"),
                            ["reason"] =
                                reason.Trim()
                        }));

                WriteJson(
                    response,
                    new
                    {
                        status =
                            result.Created
                                ? "created"
                                : "duplicate",
                        transaction =
                            TransactionPayload(
                                result.Transaction),
                        correlation_id =
                            correlationId
                    },
                    result.Created
                        ? HttpStatusCode.Created
                        : HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private void HandleAccountStatus(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid accountId,
            NexPrincipal principal)
        {
            NexEconomyService economy =
                RequireEconomy(response);

            if (economy == null)
                return;

            if (!TryBody(
                    request,
                    response,
                    out JsonElement body))
            {
                return;
            }

            string rawStatus =
                GetOptionalString(
                    body,
                    "status");
            string reason =
                GetOptionalString(
                    body,
                    "reason");

            if (!Enum.TryParse(
                    rawStatus,
                    true,
                    out NexLedgerAccountStatus status) ||
                !Enum.IsDefined(
                    typeof(NexLedgerAccountStatus),
                    status))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_account_status",
                    "status must be active, locked or closed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "status_reason_required",
                    "reason is required.");
                return;
            }

            try
            {
                string correlationId =
                    Correlation(response);

                NexLedgerAccountState state =
                    economy.SetAccountStatus(
                        accountId,
                        status,
                        principal.Subject,
                        reason);

                m_Audit.Record(
                    new NexAuditEvent(
                        principal.Subject,
                        "economy.account.status",
                        "ledger-account:" +
                        accountId.ToString("D"),
                        correlationId,
                        new Dictionary<string, string>
                        {
                            ["status"] =
                                state.Status.ToString(),
                            ["version"] =
                                state.Version.ToString(),
                            ["reason"] =
                                reason.Trim()
                        }));

                WriteJson(
                    response,
                    new
                    {
                        state =
                            StatePayload(
                                state),
                        correlation_id =
                            correlationId
                    });
            }
            catch (Exception e)
            {
                WriteEconomyFailure(
                    response,
                    e);
            }
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope,
            out NexPrincipal principal,
            out UserAccount account)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    scope,
                    out principal,
                    out account,
                    out int statusCode,
                    out string error))
            {
                return true;
            }

            response.AddHeader(
                "WWW-Authenticate",
                "Bearer");

            WriteError(
                response,
                (HttpStatusCode)statusCode,
                error,
                "Authentication or " +
                scope +
                " authorization is required.");

            return false;
        }

        private NexEconomyService RequireEconomy(
            IOSHttpResponse response)
        {
            NexEconomyService economy =
                m_EconomyProvider();

            if (economy != null)
                return economy;

            WriteError(
                response,
                HttpStatusCode.ServiceUnavailable,
                "economy_unavailable",
                "The authoritative NV$ ledger is not enabled on this Robust process.");

            return null;
        }

        private bool TryResolveReadAccount(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticatedAccount,
            out Guid accountId)
        {
            accountId =
                Guid.Empty;

            string raw =
                (request?.QueryString?["account_id"] ??
                 string.Empty)
                    .Trim();

            if (authenticatedAccount != null)
            {
                accountId =
                    authenticatedAccount.PrincipalID.Guid;

                if (raw.Length > 0 &&
                    (!Guid.TryParse(raw, out Guid requested) ||
                     requested != accountId) &&
                    !principal.HasScope(NexScopes.AdminAll))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Forbidden,
                        "economy_account_forbidden",
                        "Resident tokens may read only their own NV$ account data.");
                    return false;
                }

                if (raw.Length > 0 &&
                    principal.HasScope(NexScopes.AdminAll) &&
                    Guid.TryParse(raw, out Guid adminRequested) &&
                    adminRequested != Guid.Empty)
                {
                    accountId =
                        adminRequested;
                }

                return true;
            }

            if (!Guid.TryParse(
                    raw,
                    out accountId) ||
                accountId == Guid.Empty)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "account_id_required",
                    "Service/API-key account reads require account_id.");
                return false;
            }

            return true;
        }

        private NexLedgerAccount EnsureAccountIfResident(
            NexEconomyService economy,
            Guid accountId)
        {
            NexLedgerAccount existing =
                economy.GetAccount(
                    accountId);

            if (existing != null)
                return existing;

            UserAccount account =
                m_UserAccounts.GetUserAccount(
                    UUID.Zero,
                    new UUID(accountId));

            if (account == null)
                return null;

            string displayName =
                ((account.FirstName ?? string.Empty) +
                 " " +
                 (account.LastName ?? string.Empty))
                    .Trim();

            return economy.EnsureResidentAccount(
                accountId,
                displayName);
        }

        private static object AccountPayload(
            NexLedgerAccount account,
            long balance,
            NexLedgerAccountState state) =>
            new
            {
                account_id =
                    account.AccountId.ToString("D"),
                account_class =
                    account.AccountClass
                        .ToString()
                        .ToLowerInvariant(),
                reference =
                    account.Reference,
                display_name =
                    account.DisplayName,
                currency_code =
                    account.CurrencyCode,
                balance,
                status =
                    state.Status
                        .ToString()
                        .ToLowerInvariant(),
                state_version =
                    state.Version,
                created_at =
                    account.CreatedAt
            };

        private static object VirtualAccountPayload(
            NexVirtualBankAccount account) =>
            new
            {
                account_id =
                    account.AccountId.ToString("D"),
                identifier =
                    account.Identifier,
                scheme =
                    NexVirtualBankAccount.Scheme,
                scope =
                    "opengenesislink_virtual_only",
                disclaimer =
                    "OpenGenesisLINK-only virtual identifier; not a real-world bank account or IBAN.",
                created_at =
                    account.CreatedAt
            };

        private static object TransactionPayload(
            NexLedgerTransaction transaction) =>
            new
            {
                transaction_id =
                    transaction.TransactionId.ToString("D"),
                kind =
                    transaction.Kind,
                reference =
                    transaction.Reference,
                correlation_id =
                    transaction.CorrelationId,
                currency_code =
                    transaction.CurrencyCode,
                occurred_at =
                    transaction.OccurredAt,
                metadata =
                    transaction.Metadata,
                postings =
                    transaction.Postings
                        .Select(x => new
                        {
                            posting_id =
                                x.PostingId.ToString("D"),
                            account_id =
                                x.AccountId.ToString("D"),
                            side =
                                x.Side
                                    .ToString()
                                    .ToLowerInvariant(),
                            amount =
                                x.AmountMinor,
                            memo =
                                x.Memo
                        })
                        .ToArray()
            };

        private static object StatePayload(
            NexLedgerAccountState state) =>
            new
            {
                account_id =
                    state.AccountId.ToString("D"),
                status =
                    state.Status
                        .ToString()
                        .ToLowerInvariant(),
                version =
                    state.Version,
                changed_at =
                    state.ChangedAt,
                changed_by =
                    state.ChangedBy,
                reason =
                    state.Reason
            };

        private static object CurrencyPayload() =>
            new
            {
                code =
                    NexLedgerCurrency.Code,
                symbol =
                    NexLedgerCurrency.Symbol,
                minor_units =
                    NexLedgerCurrency.MinorUnits
            };

        private static bool TryBody(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonElement body)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        request.InputStream);

                body =
                    document.RootElement.Clone();

                if (body.ValueKind ==
                    JsonValueKind.Object)
                {
                    return true;
                }
            }
            catch
            {
            }

            body =
                default;

            WriteError(
                response,
                HttpStatusCode.BadRequest,
                "invalid_json",
                "A valid JSON object is required.");

            return false;
        }

        private static bool TryGuid(
            JsonElement body,
            string property,
            out Guid value)
        {
            value =
                Guid.Empty;

            return
                body.TryGetProperty(
                    property,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.String &&
                Guid.TryParse(
                    element.GetString(),
                    out value) &&
                value != Guid.Empty;
        }

        private static bool TryPositiveInt64(
            JsonElement body,
            string property,
            out long value)
        {
            value =
                0;

            return
                body.TryGetProperty(
                    property,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.Number &&
                element.TryGetInt64(
                    out value) &&
                value > 0;
        }

        private static string GetOptionalString(
            JsonElement body,
            string property)
        {
            if (!body.TryGetProperty(
                    property,
                    out JsonElement element) ||
                element.ValueKind !=
                    JsonValueKind.String)
            {
                return string.Empty;
            }

            return
                (element.GetString() ??
                 string.Empty)
                    .Trim();
        }

        private static Guid DeterministicGuid(
            string value)
        {
            byte[] hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        value ??
                        string.Empty));

            byte[] guid =
                new byte[16];

            Array.Copy(
                hash,
                guid,
                guid.Length);

            return new Guid(guid);
        }

        private static string Correlation(
            IOSHttpResponse response) =>
            NexApiRequestContext.Ensure(
                response);

        private static void MethodNotAllowed(
            IOSHttpResponse response,
            string expected) =>
            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                expected +
                " is required.");

        private static void WriteEconomyFailure(
            IOSHttpResponse response,
            Exception exception)
        {
            if (exception is
                NexLedgerPolicyException)
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "economy_policy_rejected",
                    exception.Message);
                return;
            }

            if (exception is
                NexLedgerConflictException)
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "economy_conflict",
                    exception.Message);
                return;
            }

            if (exception is
                NexLedgerValidationException ||
                exception is
                ArgumentException)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "economy_validation_failed",
                    exception.Message);
                return;
            }

            WriteError(
                response,
                HttpStatusCode.InternalServerError,
                "economy_internal_error",
                "The NV$ economy operation failed.");
        }

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string message)
        {
            WriteJson(
                response,
                new
                {
                    error,
                    message,
                    correlation_id =
                        Correlation(
                            response)
                },
                status);
        }

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status =
                HttpStatusCode.OK)
        {
            response.KeepAlive =
                false;
            response.StatusCode =
                (int)status;
            response.ContentType =
                "application/json; charset=utf-8";
            response.RawBuffer =
                JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    s_Json);
        }
    }
}
