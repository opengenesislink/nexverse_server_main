// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
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
    internal sealed class NexBankingCommerceApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IUserAccountService m_UserAccounts;
        private readonly INexAuditSink m_Audit;
        private readonly Func<NexEconomyService> m_EconomyProvider;

        public NexBankingCommerceApi(
            NexApiAuthenticator authenticator,
            IUserAccountService userAccounts,
            INexAuditSink audit,
            Func<NexEconomyService> economyProvider)
        {
            m_Authenticator = authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_UserAccounts = userAccounts ??
                throw new ArgumentNullException(nameof(userAccounts));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_EconomyProvider = economyProvider ??
                throw new ArgumentNullException(nameof(economyProvider));
        }

        public void HandleBanking(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path = NormalizePath(request);
            string method = request?.HttpMethod ?? string.Empty;

            if (path == "/api/v1/banking/transactions")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                    return;

                HandleTransactions(request, response, principal, account);
                return;
            }

            if (path == "/api/v1/banking/statements")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                    return;

                HandleStatement(request, response, principal, account);
                return;
            }

            if (path == "/api/v1/banking/reconciliation")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                    return;

                HandleReconciliation(request, response, principal, account);
                return;
            }

            if (path == "/api/v1/banking/transfers")
            {
                if (!RequireMethod(method, "POST", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                    return;

                HandleBankingTransfer(request, response, principal, account);
                return;
            }

            if (path == "/api/v1/banking/payment-requests")
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleListPaymentRequests(request, response, principal, account);
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleCreatePaymentRequest(request, response, principal, account);
                    return;
                }

                MethodNotAllowed(response, "GET or POST");
                return;
            }

            const string paymentPrefix = "/api/v1/banking/payment-requests/";
            if (path.StartsWith(paymentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    path.Substring(paymentPrefix.Length)
                        .Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 2 ||
                    !Guid.TryParse(parts[0], out Guid requestId) ||
                    requestId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_payment_request_id", "A valid payment request UUID is required.");
                    return;
                }

                if (parts[1].Equals("pay", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandlePayPaymentRequest(response, requestId, principal, account);
                    return;
                }

                if (parts[1].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleCancelPaymentRequest(response, requestId, principal, account);
                    return;
                }
            }

            const string accountPrefix = "/api/v1/banking/accounts/";
            if (path.StartsWith(accountPrefix, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/policy", StringComparison.OrdinalIgnoreCase))
            {
                string raw =
                    path.Substring(
                        accountPrefix.Length,
                        path.Length - accountPrefix.Length - "/policy".Length)
                    .Trim('/');

                if (!Guid.TryParse(raw, out Guid accountId) || accountId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_account_id", "A valid account UUID is required.");
                    return;
                }

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                        return;
                    if (!CanReadAccount(response, principal, account, accountId))
                        return;
                    HandleGetPolicy(response, accountId);
                    return;
                }

                if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out _))
                        return;
                    HandleSetPolicy(request, response, accountId, principal);
                    return;
                }

                MethodNotAllowed(response, "GET or PUT");
                return;
            }

            const string escrowPrefix = "/api/v1/banking/escrow/";
            if (path.StartsWith(escrowPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    path.Substring(escrowPrefix.Length)
                        .Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 2 ||
                    !Guid.TryParse(parts[0], out Guid escrowId) ||
                    escrowId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_escrow_id", "A valid escrow UUID is required.");
                    return;
                }

                if (!RequireMethod(method, "POST", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                    return;

                if (parts[1].Equals("fund", StringComparison.OrdinalIgnoreCase))
                {
                    HandleEscrowFund(request, response, escrowId, principal, account);
                    return;
                }

                if (parts[1].Equals("release", StringComparison.OrdinalIgnoreCase))
                {
                    HandleEscrowRelease(request, response, escrowId, principal, account);
                    return;
                }
            }

            NotFound(response);
        }

        public void HandleCommerce(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path = NormalizePath(request);
            string method = request?.HttpMethod ?? string.Empty;

            if (path == "/api/v1/commerce/orders")
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleListOrders(request, response, principal, account);
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleCreateOrder(request, response, principal, account);
                    return;
                }

                MethodNotAllowed(response, "GET or POST");
                return;
            }

            const string prefix = "/api/v1/commerce/orders/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = path.Substring(prefix.Length);
                string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 1 ||
                    !Guid.TryParse(parts[0], out Guid orderId) ||
                    orderId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_order_id", "A valid order UUID is required.");
                    return;
                }

                if (parts.Length == 1)
                {
                    if (!RequireMethod(method, "GET", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleGetOrder(response, orderId, principal, account);
                    return;
                }

                if (parts.Length == 2 &&
                    parts[1].Equals("refund", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    if (account != null &&
                        !principal.HasScope(NexScopes.AdminAll))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.Forbidden,
                            "commerce_refund_forbidden",
                            "Commerce refunds require a trusted service or administrator.");
                        return;
                    }
                    HandleRefundOrder(request, response, orderId, principal);
                    return;
                }
            }

            NotFound(response);
        }

        public void HandleLandCommerce(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path = NormalizePath(request);
            string method = request?.HttpMethod ?? string.Empty;

            if (path == "/api/v1/land-commerce/listings")
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.EconomyRead, out _, out _))
                        return;
                    HandleSearchLandListings(request, response);
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out _))
                        return;
                    HandleCreateLandListing(request, response, principal);
                    return;
                }

                MethodNotAllowed(response, "GET or POST");
                return;
            }

            const string listingPrefix = "/api/v1/land-commerce/listings/";
            if (path.StartsWith(listingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    path.Substring(listingPrefix.Length)
                        .Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 2 ||
                    !Guid.TryParse(parts[0], out Guid listingId) ||
                    listingId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_listing_id", "A valid listing UUID is required.");
                    return;
                }

                if (parts[1].Equals("deactivate", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out _))
                        return;
                    HandleDeactivateLandListing(request, response, listingId, principal);
                    return;
                }

                if (parts[1].Equals("purchase", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandlePurchaseLandListing(request, response, listingId, principal, account);
                    return;
                }

                if (parts[1].Equals("lease", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(method, "POST", response))
                        return;
                    if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                        return;
                    HandleCreateLandLease(request, response, listingId, principal, account);
                    return;
                }
            }

            if (path == "/api/v1/land-commerce/leases")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyRead, out NexPrincipal principal, out UserAccount account))
                    return;
                HandleListLandLeases(request, response, principal, account);
                return;
            }

            const string leasePrefix = "/api/v1/land-commerce/leases/";
            if (path.StartsWith(leasePrefix, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/pay", StringComparison.OrdinalIgnoreCase))
            {
                string raw =
                    path.Substring(
                        leasePrefix.Length,
                        path.Length - leasePrefix.Length - "/pay".Length)
                    .Trim('/');

                if (!Guid.TryParse(raw, out Guid leaseId) || leaseId == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_lease_id", "A valid lease UUID is required.");
                    return;
                }

                if (!RequireMethod(method, "POST", response))
                    return;
                if (!Authenticate(request, response, NexScopes.EconomyTransfer, out NexPrincipal principal, out UserAccount account))
                    return;

                HandlePayLandRent(request, response, leaseId, principal, account);
                return;
            }

            NotFound(response);
        }

        private void HandleTransactions(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);
                int offset = QueryInt(request, "offset", 0, 0, 1000000);
                int limit = QueryInt(request, "limit", 100, 1, 1000);
                DateTimeOffset? from = QueryDate(request, "from");
                DateTimeOffset? to = QueryDate(request, "to");

                WriteJson(response, new
                {
                    account_id = accountId.ToString("D"),
                    transactions = economy.ListTransactions(accountId, from, to, offset, limit)
                        .Select(TransactionPayload)
                        .ToArray(),
                    offset,
                    limit,
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleStatement(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);

                DateTimeOffset to =
                    QueryDate(request, "to") ?? DateTimeOffset.UtcNow;
                DateTimeOffset from =
                    QueryDate(request, "from") ?? to.AddDays(-30);

                NexBankStatement statement =
                    economy.GetStatement(
                        accountId,
                        from,
                        to,
                        QueryInt(request, "offset", 0, 0, 1000000),
                        QueryInt(request, "limit", 500, 1, 1000));

                WriteJson(response, new
                {
                    statement = new
                    {
                        account_id = statement.AccountId.ToString("D"),
                        from = statement.From,
                        to = statement.To,
                        opening_balance = statement.OpeningBalance,
                        closing_balance = statement.ClosingBalance,
                        transactions = statement.Transactions.Select(TransactionPayload).ToArray()
                    },
                    currency = CurrencyPayload(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleReconciliation(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);
                NexReconciliationReport report =
                    economy.ReconcileAccount(accountId);

                WriteJson(response, new
                {
                    reconciliation = new
                    {
                        account_id = report.AccountId.ToString("D"),
                        derived_balance = report.DerivedBalance,
                        recomputed_balance = report.RecomputedBalance,
                        transaction_count = report.TransactionCount,
                        balanced = report.Balanced
                    },
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleBankingTransfer(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid sourceId =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "from_account_id");

                if (authenticated != null &&
                    body.TryGetProperty("from_account_id", out JsonElement sourceElement) &&
                    sourceElement.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(sourceElement.GetString(), out Guid requestedSource) &&
                    requestedSource != sourceId &&
                    !principal.HasScope(NexScopes.AdminAll))
                {
                    WriteError(response, HttpStatusCode.Forbidden, "banking_source_forbidden", "Resident tokens may debit only their own NV$ wallet.");
                    return;
                }

                Guid destinationId = Guid.Empty;

                if (body.TryGetProperty("to_account_id", out JsonElement destinationElement) &&
                    destinationElement.ValueKind == JsonValueKind.String)
                {
                    Guid.TryParse(destinationElement.GetString(), out destinationId);
                }

                if (destinationId == Guid.Empty)
                {
                    string nvban = BodyString(body, "to_nvban");
                    if (nvban.Length == 0)
                        throw new ArgumentException("to_account_id or to_nvban is required.");

                    NexVirtualBankAccount target =
                        economy.ResolveVirtualBankAccount(nvban) ??
                        throw new NexLedgerPolicyException("NVBAN destination was not found.");
                    destinationId = target.AccountId;
                }

                long amount = BodyPositiveLong(body, "amount");
                string reference = BodyString(body, "reference");
                if (reference.Length == 0)
                    reference = "NV$ banking transfer";

                EnsureResidentIfKnown(economy, sourceId);
                EnsureResidentIfKnown(economy, destinationId);

                string idempotency =
                    (request?.Headers?["Idempotency-Key"] ?? string.Empty).Trim();
                if (idempotency.Length == 0 || idempotency.Length > 128)
                    throw new ArgumentException("Idempotency-Key is required and may contain at most 128 characters.");

                Guid transactionId =
                    DeterministicGuid(
                        "banking-transfer:" +
                        principal.Subject +
                        ":" +
                        idempotency);

                NexLedgerAppendResult result =
                    economy.BankingTransfer(
                        sourceId,
                        destinationId,
                        amount,
                        reference,
                        Correlation(response),
                        transactionId);

                Audit(
                    principal,
                    result.Created ? "banking.transfer.created" : "banking.transfer.duplicate",
                    "ledger-transaction:" + result.Transaction.TransactionId.ToString("D"),
                    response);

                WriteJson(
                    response,
                    new
                    {
                        status = result.Created ? "created" : "duplicate",
                        transaction = TransactionPayload(result.Transaction),
                        balances = new
                        {
                            source = economy.GetBalance(sourceId),
                            destination = economy.GetBalance(destinationId)
                        },
                        currency = CurrencyPayload(),
                        correlation_id = Correlation(response)
                    },
                    result.Created ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleCreatePaymentRequest(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid payee =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "payee_account_id");

                if (authenticated != null &&
                    body.TryGetProperty("payee_account_id", out JsonElement payeeElement) &&
                    payeeElement.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(payeeElement.GetString(), out Guid requestedPayee) &&
                    requestedPayee != payee &&
                    !principal.HasScope(NexScopes.AdminAll))
                {
                    throw new NexLedgerPolicyException("Resident tokens may create requests only for their own payee wallet.");
                }

                Guid payer = BodyGuid(body, "payer_account_id");
                long amount = BodyPositiveLong(body, "amount");
                string reference = BodyString(body, "reference");
                DateTimeOffset? expires = BodyDate(body, "expires_at");

                EnsureResidentIfKnown(economy, payee);
                EnsureResidentIfKnown(economy, payer);

                NexPaymentRequest created =
                    economy.CreatePaymentRequest(
                        payee,
                        payer,
                        amount,
                        reference,
                        expires);

                Audit(principal, "banking.payment_request.created", "payment-request:" + created.RequestId.ToString("D"), response);

                WriteJson(response, new
                {
                    payment_request = PaymentRequestPayload(created),
                    correlation_id = Correlation(response)
                }, HttpStatusCode.Created);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleListPaymentRequests(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);

                WriteJson(response, new
                {
                    account_id = accountId.ToString("D"),
                    payment_requests =
                        economy.ListPaymentRequests(
                            accountId,
                            QueryInt(request, "offset", 0, 0, 1000000),
                            QueryInt(request, "limit", 100, 1, 1000))
                        .Select(PaymentRequestPayload)
                        .ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandlePayPaymentRequest(
            IOSHttpResponse response,
            Guid requestId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null)
                return;

            try
            {
                NexPaymentRequest request =
                    economy.GetPaymentRequest(requestId) ??
                    throw new NexLedgerPolicyException("Payment request was not found.");

                Guid payer =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : request.PayerAccountId;

                if (authenticated == null && !principal.HasScope(NexScopes.AdminAll))
                    payer = request.PayerAccountId;

                EnsureResidentIfKnown(economy, payer);

                NexPaymentRequest paid =
                    economy.PayPaymentRequest(
                        requestId,
                        payer,
                        Correlation(response));

                Audit(principal, "banking.payment_request.paid", "payment-request:" + requestId.ToString("D"), response);

                WriteJson(response, new
                {
                    payment_request = PaymentRequestPayload(paid),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleCancelPaymentRequest(
            IOSHttpResponse response,
            Guid requestId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null)
                return;

            try
            {
                NexPaymentRequest request =
                    economy.GetPaymentRequest(requestId) ??
                    throw new NexLedgerPolicyException("Payment request was not found.");

                Guid payee =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : request.PayeeAccountId;

                NexPaymentRequest cancelled =
                    economy.CancelPaymentRequest(
                        requestId,
                        payee);

                Audit(principal, "banking.payment_request.cancelled", "payment-request:" + requestId.ToString("D"), response);

                WriteJson(response, new
                {
                    payment_request = PaymentRequestPayload(cancelled),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleGetPolicy(
            IOSHttpResponse response,
            Guid accountId)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null)
                return;

            try
            {
                WriteJson(response, new
                {
                    policy = TransferPolicyPayload(economy.GetTransferPolicy(accountId)),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleSetPolicy(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid accountId,
            NexPrincipal principal)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                long maxPerTransfer = BodyNonNegativeLong(body, "max_per_transfer", 0);
                long dailyLimit = BodyNonNegativeLong(body, "daily_outgoing_limit", 0);
                long flatFee = BodyNonNegativeLong(body, "flat_fee", 0);
                Guid feeAccount = BodyOptionalGuid(body, "fee_account_id");

                NexAccountTransferPolicy policy =
                    economy.SetTransferPolicy(
                        new NexAccountTransferPolicy(
                            accountId,
                            maxPerTransfer,
                            dailyLimit,
                            flatFee,
                            feeAccount));

                Audit(principal, "banking.policy.updated", "ledger-account:" + accountId.ToString("D"), response);

                WriteJson(response, new
                {
                    policy = TransferPolicyPayload(policy),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleEscrowFund(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid escrowId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid from =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "from_account_id");
                long amount = BodyPositiveLong(body, "amount");
                string reference = BodyString(body, "reference");
                if (reference.Length == 0)
                    reference = "NV$ escrow funding";

                EnsureResidentIfKnown(economy, from);

                string key = RequiredIdempotency(request);
                NexLedgerAppendResult result =
                    economy.FundEscrow(
                        from,
                        escrowId,
                        amount,
                        reference,
                        Correlation(response),
                        DeterministicGuid("escrow-fund:" + principal.Subject + ":" + key));

                Audit(principal, "banking.escrow.funded", "escrow:" + escrowId.ToString("D"), response);

                WriteJson(response, new
                {
                    transaction = TransactionPayload(result.Transaction),
                    escrow_balance = economy.GetBalance(escrowId),
                    correlation_id = Correlation(response)
                }, result.Created ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleEscrowRelease(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid escrowId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                if (authenticated != null && !principal.HasScope(NexScopes.AdminAll))
                    throw new NexLedgerPolicyException("Escrow release requires a trusted service or administrator.");

                Guid to = BodyGuid(body, "to_account_id");
                long amount = BodyPositiveLong(body, "amount");
                string reference = BodyString(body, "reference");
                if (reference.Length == 0)
                    reference = "NV$ escrow release";

                EnsureResidentIfKnown(economy, to);

                string key = RequiredIdempotency(request);
                NexLedgerAppendResult result =
                    economy.ReleaseEscrow(
                        escrowId,
                        to,
                        amount,
                        reference,
                        Correlation(response),
                        DeterministicGuid("escrow-release:" + principal.Subject + ":" + key));

                Audit(principal, "banking.escrow.released", "escrow:" + escrowId.ToString("D"), response);

                WriteJson(response, new
                {
                    transaction = TransactionPayload(result.Transaction),
                    escrow_balance = economy.GetBalance(escrowId),
                    correlation_id = Correlation(response)
                }, result.Created ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleCreateOrder(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                NexCommerceKind kind =
                    ParseEnum<NexCommerceKind>(
                        BodyString(body, "kind"),
                        "kind");

                Guid buyer =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "buyer_account_id");
                Guid seller = BodyGuid(body, "seller_account_id");
                long amount = BodyPositiveLong(body, "amount");
                string reference = BodyString(body, "reference");
                string external = BodyString(body, "external_reference");
                Guid orderId =
                    BodyOptionalGuid(body, "order_id");
                if (orderId == Guid.Empty)
                    orderId = Guid.NewGuid();

                EnsureResidentIfKnown(economy, buyer);
                EnsureResidentIfKnown(economy, seller);

                NexCommerceOrder order =
                    economy.ExecuteCommerceOrder(
                        orderId,
                        kind,
                        buyer,
                        seller,
                        amount,
                        reference,
                        external,
                        Correlation(response));

                Audit(principal, "commerce.order.completed", "commerce-order:" + order.OrderId.ToString("D"), response);

                WriteJson(response, new
                {
                    order = CommerceOrderPayload(order),
                    correlation_id = Correlation(response)
                }, HttpStatusCode.Created);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleListOrders(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);

                WriteJson(response, new
                {
                    account_id = accountId.ToString("D"),
                    orders = economy.ListCommerceOrders(
                            accountId,
                            QueryInt(request, "offset", 0, 0, 1000000),
                            QueryInt(request, "limit", 100, 1, 1000))
                        .Select(CommerceOrderPayload)
                        .ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleGetOrder(
            IOSHttpResponse response,
            Guid orderId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null)
                return;

            try
            {
                NexCommerceOrder order =
                    economy.GetCommerceOrder(orderId) ??
                    throw new NexLedgerPolicyException("Commerce order was not found.");

                if (authenticated != null &&
                    !principal.HasScope(NexScopes.AdminAll) &&
                    order.BuyerAccountId != authenticated.PrincipalID.Guid &&
                    order.SellerAccountId != authenticated.PrincipalID.Guid)
                {
                    WriteError(response, HttpStatusCode.Forbidden, "commerce_order_forbidden", "Resident tokens may read only their own commerce orders.");
                    return;
                }

                WriteJson(response, new
                {
                    order = CommerceOrderPayload(order),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleRefundOrder(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid orderId,
            NexPrincipal principal)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                string reason = BodyString(body, "reason");
                if (reason.Length == 0)
                    throw new ArgumentException("reason is required.");

                NexCommerceOrder order =
                    economy.RefundCommerceOrder(
                        orderId,
                        principal.Subject,
                        reason);

                Audit(principal, "commerce.order.refunded", "commerce-order:" + orderId.ToString("D"), response);

                WriteJson(response, new
                {
                    order = CommerceOrderPayload(order),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleSearchLandListings(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null)
                return;

            try
            {
                NexLandListingType? type = null;
                string rawType = (request?.QueryString?["type"] ?? string.Empty).Trim();
                if (rawType.Length > 0)
                    type = ParseEnum<NexLandListingType>(rawType, "type");

                WriteJson(response, new
                {
                    listings = economy.SearchLandListings(
                            request?.QueryString?["q"] ?? string.Empty,
                            type,
                            QueryInt(request, "offset", 0, 0, 1000000),
                            QueryInt(request, "limit", 100, 1, 1000))
                        .Select(LandListingPayload)
                        .ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleCreateLandListing(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                NexLandListing listing =
                    new NexLandListing(
                        BodyOptionalGuid(body, "listing_id") is Guid id && id != Guid.Empty
                            ? id
                            : Guid.NewGuid(),
                        ParseEnum<NexLandListingType>(BodyString(body, "type"), "type"),
                        BodyGuid(body, "region_id"),
                        BodyString(body, "region_name"),
                        BodyGuid(body, "parcel_id"),
                        BodyPositiveInt(body, "parcel_local_id"),
                        BodyString(body, "parcel_name"),
                        BodyGuid(body, "seller_account_id"),
                        BodyOptionalGuid(body, "estate_id"),
                        BodyPositiveInt(body, "area"),
                        BodyPositiveLong(body, "price"),
                        BodyNonNegativeInt(body, "rental_period_days", 0));

                EnsureResidentIfKnown(economy, listing.SellerAccountId);

                NexLandListing created =
                    economy.CreateLandListing(listing);

                Audit(principal, "land.listing.created", "land-listing:" + created.ListingId.ToString("D"), response);

                WriteJson(response, new
                {
                    listing = LandListingPayload(created),
                    correlation_id = Correlation(response)
                }, HttpStatusCode.Created);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleDeactivateLandListing(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid listingId,
            NexPrincipal principal)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid seller = BodyGuid(body, "seller_account_id");
                NexLandListing listing =
                    economy.DeactivateLandListing(
                        listingId,
                        seller);

                Audit(principal, "land.listing.deactivated", "land-listing:" + listingId.ToString("D"), response);

                WriteJson(response, new
                {
                    listing = LandListingPayload(listing),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandlePurchaseLandListing(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid listingId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid buyer =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "buyer_account_id");
                Guid orderId =
                    BodyOptionalGuid(body, "order_id");
                if (orderId == Guid.Empty)
                    orderId = Guid.NewGuid();

                EnsureResidentIfKnown(economy, buyer);

                NexCommerceOrder order =
                    economy.PurchaseLandListing(
                        listingId,
                        buyer,
                        orderId,
                        Correlation(response));

                Audit(principal, "land.purchase.completed", "land-listing:" + listingId.ToString("D"), response);

                WriteJson(response, new
                {
                    order = CommerceOrderPayload(order),
                    correlation_id = Correlation(response)
                }, HttpStatusCode.Created);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleCreateLandLease(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid listingId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid tenant =
                    authenticated != null
                        ? authenticated.PrincipalID.Guid
                        : BodyGuid(body, "tenant_account_id");
                int periods = BodyPositiveInt(body, "periods");
                Guid orderId =
                    BodyOptionalGuid(body, "order_id");
                if (orderId == Guid.Empty)
                    orderId = Guid.NewGuid();

                EnsureResidentIfKnown(economy, tenant);

                NexLandLease lease =
                    economy.CreateLandLease(
                        listingId,
                        tenant,
                        periods,
                        orderId,
                        Correlation(response));

                Audit(principal, "land.lease.created", "land-lease:" + lease.LeaseId.ToString("D"), response);

                WriteJson(response, new
                {
                    lease = LandLeasePayload(lease),
                    correlation_id = Correlation(response)
                }, HttpStatusCode.Created);
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandleListLandLeases(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryResolveReadAccount(request, response, principal, authenticated, out Guid accountId))
                return;

            try
            {
                EnsureResidentIfKnown(economy, accountId);

                WriteJson(response, new
                {
                    leases = economy.ListLandLeases(
                            accountId,
                            QueryInt(request, "offset", 0, 0, 1000000),
                            QueryInt(request, "limit", 100, 1, 1000))
                        .Select(LandLeasePayload)
                        .ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private void HandlePayLandRent(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Guid leaseId,
            NexPrincipal principal,
            UserAccount authenticated)
        {
            NexEconomyService economy = RequireEconomy(response);
            if (economy == null || !TryBody(request, response, out JsonElement body))
                return;

            try
            {
                Guid orderId =
                    BodyOptionalGuid(body, "order_id");
                if (orderId == Guid.Empty)
                    orderId = Guid.NewGuid();

                NexLandLease existing =
                    economy.GetLandLease(
                        leaseId) ??
                    throw new NexLedgerPolicyException("Land lease was not found.");

                if (authenticated != null &&
                    existing.TenantAccountId != authenticated.PrincipalID.Guid &&
                    !principal.HasScope(NexScopes.AdminAll))
                {
                    throw new NexLedgerPolicyException("Resident tokens may pay only their own lease.");
                }

                NexLandLease lease =
                    economy.PayLandRent(
                        leaseId,
                        orderId,
                        Correlation(response));

                Audit(principal, "land.rent.paid", "land-lease:" + leaseId.ToString("D"), response);

                WriteJson(response, new
                {
                    lease = LandLeasePayload(lease),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        private NexEconomyService RequireEconomy(IOSHttpResponse response)
        {
            NexEconomyService economy = m_EconomyProvider();

            if (economy != null)
                return economy;

            WriteError(response, HttpStatusCode.ServiceUnavailable, "economy_unavailable", "The authoritative NV$ economy is not enabled.");
            return null;
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

            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteError(
                response,
                (HttpStatusCode)statusCode,
                error,
                "Authentication or " + scope + " authorization is required.");
            return false;
        }

        private bool TryResolveReadAccount(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated,
            out Guid accountId)
        {
            accountId = Guid.Empty;
            string raw =
                (request?.QueryString?["account_id"] ?? string.Empty).Trim();

            if (authenticated != null)
            {
                accountId = authenticated.PrincipalID.Guid;

                if (raw.Length > 0 &&
                    Guid.TryParse(raw, out Guid requested) &&
                    requested != accountId)
                {
                    if (!principal.HasScope(NexScopes.AdminAll))
                    {
                        WriteError(response, HttpStatusCode.Forbidden, "banking_account_forbidden", "Resident tokens may read only their own banking data.");
                        return false;
                    }

                    accountId = requested;
                }

                return true;
            }

            if (!Guid.TryParse(raw, out accountId) || accountId == Guid.Empty)
            {
                WriteError(response, HttpStatusCode.BadRequest, "account_id_required", "Service/API-key reads require account_id.");
                return false;
            }

            return true;
        }

        private static bool CanReadAccount(
            IOSHttpResponse response,
            NexPrincipal principal,
            UserAccount authenticated,
            Guid accountId)
        {
            if (authenticated == null ||
                authenticated.PrincipalID.Guid == accountId ||
                principal.HasScope(NexScopes.AdminAll))
            {
                return true;
            }

            WriteError(response, HttpStatusCode.Forbidden, "banking_account_forbidden", "Resident tokens may read only their own banking data.");
            return false;
        }

        private NexLedgerAccount EnsureResidentIfKnown(
            NexEconomyService economy,
            Guid accountId)
        {
            NexLedgerAccount existing =
                economy.GetAccount(accountId);

            if (existing != null)
                return existing;

            UserAccount account =
                m_UserAccounts.GetUserAccount(
                    UUID.Zero,
                    new UUID(accountId));

            if (account == null)
                return null;

            return economy.EnsureResidentAccount(
                accountId,
                ((account.FirstName ?? string.Empty) + " " + (account.LastName ?? string.Empty)).Trim());
        }

        private void Audit(
            NexPrincipal principal,
            string action,
            string target,
            IOSHttpResponse response)
        {
            m_Audit.Record(
                new NexAuditEvent(
                    principal.Subject,
                    action,
                    target,
                    Correlation(response),
                    new Dictionary<string, string>()));
        }

        private static object TransactionPayload(NexLedgerTransaction transaction) =>
            new
            {
                transaction_id = transaction.TransactionId.ToString("D"),
                kind = transaction.Kind,
                reference = transaction.Reference,
                correlation_id = transaction.CorrelationId,
                currency_code = transaction.CurrencyCode,
                occurred_at = transaction.OccurredAt,
                metadata = transaction.Metadata,
                postings = transaction.Postings.Select(x => new
                {
                    posting_id = x.PostingId.ToString("D"),
                    account_id = x.AccountId.ToString("D"),
                    side = x.Side.ToString().ToLowerInvariant(),
                    amount = x.AmountMinor,
                    memo = x.Memo
                }).ToArray()
            };

        private static object PaymentRequestPayload(NexPaymentRequest request) =>
            new
            {
                request_id = request.RequestId.ToString("D"),
                payee_account_id = request.PayeeAccountId.ToString("D"),
                payer_account_id = request.PayerAccountId.ToString("D"),
                amount = request.AmountMinor,
                reference = request.Reference,
                status = request.Status.ToString().ToLowerInvariant(),
                created_at = request.CreatedAt,
                expires_at = request.ExpiresAt,
                payment_transaction_id =
                    request.PaymentTransactionId == Guid.Empty
                        ? null
                        : request.PaymentTransactionId.ToString("D")
            };

        private static object TransferPolicyPayload(NexAccountTransferPolicy policy) =>
            new
            {
                account_id = policy.AccountId.ToString("D"),
                max_per_transfer = policy.MaxPerTransferMinor,
                daily_outgoing_limit = policy.DailyOutgoingLimitMinor,
                flat_fee = policy.FlatFeeMinor,
                fee_account_id =
                    policy.FeeAccountId == Guid.Empty
                        ? null
                        : policy.FeeAccountId.ToString("D")
            };

        private static object CommerceOrderPayload(NexCommerceOrder order) =>
            new
            {
                order_id = order.OrderId.ToString("D"),
                kind = order.Kind.ToString().ToLowerInvariant(),
                buyer_account_id = order.BuyerAccountId.ToString("D"),
                seller_account_id = order.SellerAccountId.ToString("D"),
                amount = order.AmountMinor,
                reference = order.Reference,
                external_reference = order.ExternalReference,
                status = order.Status.ToString().ToLowerInvariant(),
                payment_transaction_id =
                    order.PaymentTransactionId == Guid.Empty
                        ? null
                        : order.PaymentTransactionId.ToString("D"),
                refund_transaction_id =
                    order.RefundTransactionId == Guid.Empty
                        ? null
                        : order.RefundTransactionId.ToString("D"),
                created_at = order.CreatedAt,
                completed_at = order.CompletedAt
            };

        private static object LandListingPayload(NexLandListing listing) =>
            new
            {
                listing_id = listing.ListingId.ToString("D"),
                type = listing.ListingType.ToString().ToLowerInvariant(),
                region_id = listing.RegionId.ToString("D"),
                region_name = listing.RegionName,
                parcel_id = listing.ParcelId.ToString("D"),
                parcel_local_id = listing.ParcelLocalId,
                parcel_name = listing.ParcelName,
                seller_account_id = listing.SellerAccountId.ToString("D"),
                estate_id = listing.EstateId == Guid.Empty ? null : listing.EstateId.ToString("D"),
                area = listing.Area,
                price = listing.PriceMinor,
                rental_period_days = listing.RentalPeriodDays,
                active = listing.Active,
                created_at = listing.CreatedAt
            };

        private static object LandLeasePayload(NexLandLease lease) =>
            new
            {
                lease_id = lease.LeaseId.ToString("D"),
                listing_id = lease.ListingId.ToString("D"),
                tenant_account_id = lease.TenantAccountId.ToString("D"),
                landlord_account_id = lease.LandlordAccountId.ToString("D"),
                rent = lease.RentMinor,
                period_days = lease.PeriodDays,
                starts_at = lease.StartsAt,
                ends_at = lease.EndsAt,
                next_due_at = lease.NextDueAt,
                active = lease.Active,
                last_payment_transaction_id =
                    lease.LastPaymentTransactionId == Guid.Empty
                        ? null
                        : lease.LastPaymentTransactionId.ToString("D")
            };

        private static object CurrencyPayload() =>
            new
            {
                code = NexLedgerCurrency.Code,
                symbol = NexLedgerCurrency.Symbol,
                minor_units = NexLedgerCurrency.MinorUnits
            };

        private static string NormalizePath(IOSHttpRequest request) =>
            (request?.UriPath ?? string.Empty).TrimEnd('/');

        private static bool RequireMethod(
            string actual,
            string expected,
            IOSHttpResponse response)
        {
            if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;

            MethodNotAllowed(response, expected);
            return false;
        }

        private static bool TryBody(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonElement body)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(request.InputStream);
                body = document.RootElement.Clone();

                if (body.ValueKind == JsonValueKind.Object)
                    return true;
            }
            catch
            {
            }

            body = default;
            WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "A valid JSON object is required.");
            return false;
        }

        private static Guid BodyGuid(JsonElement body, string name)
        {
            Guid value = BodyOptionalGuid(body, name);
            if (value == Guid.Empty)
                throw new ArgumentException(name + " must be a valid non-zero UUID.");
            return value;
        }

        private static Guid BodyOptionalGuid(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind == JsonValueKind.Null)
            {
                return Guid.Empty;
            }

            if (element.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(element.GetString(), out Guid value))
            {
                throw new ArgumentException(name + " must be a UUID.");
            }

            return value;
        }

        private static string BodyString(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return (element.GetString() ?? string.Empty).Trim();
        }

        private static long BodyPositiveLong(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt64(out long value) ||
                value <= 0)
            {
                throw new ArgumentException(name + " must be a positive integer.");
            }

            return value;
        }

        private static long BodyNonNegativeLong(
            JsonElement body,
            string name,
            long defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement element))
                return defaultValue;

            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt64(out long value) ||
                value < 0)
            {
                throw new ArgumentException(name + " must be a non-negative integer.");
            }

            return value;
        }

        private static int BodyPositiveInt(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt32(out int value) ||
                value <= 0)
            {
                throw new ArgumentException(name + " must be a positive integer.");
            }

            return value;
        }

        private static int BodyNonNegativeInt(
            JsonElement body,
            string name,
            int defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement element))
                return defaultValue;

            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt32(out int value) ||
                value < 0)
            {
                throw new ArgumentException(name + " must be a non-negative integer.");
            }

            return value;
        }

        private static DateTimeOffset? BodyDate(JsonElement body, string name)
        {
            string value = BodyString(body, name);
            if (value.Length == 0)
                return null;

            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed))
            {
                throw new ArgumentException(name + " must be an ISO-8601 timestamp.");
            }

            return parsed.ToUniversalTime();
        }

        private static int QueryInt(
            IOSHttpRequest request,
            string name,
            int defaultValue,
            int min,
            int max)
        {
            string raw = (request?.QueryString?[name] ?? string.Empty).Trim();
            if (raw.Length == 0)
                return defaultValue;

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value < min ||
                value > max)
            {
                throw new ArgumentOutOfRangeException(name);
            }

            return value;
        }

        private static DateTimeOffset? QueryDate(
            IOSHttpRequest request,
            string name)
        {
            string raw = (request?.QueryString?[name] ?? string.Empty).Trim();
            if (raw.Length == 0)
                return null;

            if (!DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed))
            {
                throw new ArgumentException(name + " must be an ISO-8601 timestamp.");
            }

            return parsed.ToUniversalTime();
        }

        private static T ParseEnum<T>(string value, string name)
            where T : struct, Enum
        {
            if (!Enum.TryParse<T>(value, true, out T parsed) ||
                !Enum.IsDefined(typeof(T), parsed))
            {
                throw new ArgumentException(name + " contains an unsupported value.");
            }

            return parsed;
        }

        private static string RequiredIdempotency(IOSHttpRequest request)
        {
            string key =
                (request?.Headers?["Idempotency-Key"] ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 128)
                throw new ArgumentException("Idempotency-Key is required and may contain at most 128 characters.");
            return key;
        }

        private static Guid DeterministicGuid(string value)
        {
            byte[] hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value ?? string.Empty));
            byte[] bytes = new byte[16];
            Array.Copy(hash, bytes, bytes.Length);
            return new Guid(bytes);
        }

        private static string Correlation(IOSHttpResponse response) =>
            NexApiRequestContext.Ensure(response);

        private static void MethodNotAllowed(
            IOSHttpResponse response,
            string expected) =>
            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                expected + " is required.");

        private static void NotFound(IOSHttpResponse response) =>
            WriteError(
                response,
                HttpStatusCode.NotFound,
                "not_found",
                "The requested banking/commerce endpoint was not found.");

        private static void WriteFailure(
            IOSHttpResponse response,
            Exception exception)
        {
            if (exception is NexLedgerPolicyException)
            {
                WriteError(response, HttpStatusCode.Conflict, "economy_policy_rejected", exception.Message);
                return;
            }

            if (exception is NexLedgerConflictException)
            {
                WriteError(response, HttpStatusCode.Conflict, "economy_conflict", exception.Message);
                return;
            }

            if (exception is NexLedgerValidationException ||
                exception is ArgumentException ||
                exception is ArgumentOutOfRangeException)
            {
                WriteError(response, HttpStatusCode.BadRequest, "economy_validation_failed", exception.Message);
                return;
            }

            WriteError(response, HttpStatusCode.InternalServerError, "economy_internal_error", "The NV$ banking/commerce operation failed.");
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
                    correlation_id = Correlation(response)
                },
                status);
        }

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer =
                JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    s_Json);
        }
    }
}
