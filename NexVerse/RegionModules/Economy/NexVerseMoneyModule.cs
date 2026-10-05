// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Economy
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "NexVerseMoneyModule")]
    public sealed class NexVerseMoneyModule :
        IMoneyModule,
        ISharedRegionModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexVerseMoneyModule));

        private readonly object m_Sync =
            new object();
        private readonly Dictionary<ulong, Scene> m_Scenes =
            new Dictionary<ulong, Scene>();

        private HttpClient m_Http;
        private bool m_Enabled;
        private string m_WorldApiBaseUrl =
            string.Empty;
        private string m_ApiKey =
            string.Empty;
        private UUID m_FeeWalletId =
            UUID.Zero;
        private int m_UploadCharge;
        private int m_GroupCreationCharge;
        private int m_RequestTimeoutMilliseconds =
            3000;

        public event ObjectPaid OnObjectPaid;

        public string Name =>
            "NexVerseMoneyModule";

        public Type ReplaceableInterface =>
            typeof(IMoneyModule);

        public int UploadCharge =>
            m_UploadCharge;

        public int GroupCreationCharge =>
            m_GroupCreationCharge;

        public void Initialise(
            IConfigSource config)
        {
            IConfig economy =
                config?.Configs["NexEconomyViewer"];

            if (economy == null ||
                !economy.GetBoolean(
                    "Enabled",
                    false))
            {
                m_Enabled =
                    false;
                return;
            }

            m_WorldApiBaseUrl =
                economy.GetString(
                        "WorldApiBaseUrl",
                        string.Empty)
                    .Trim()
                    .TrimEnd('/');

            m_ApiKey =
                economy.GetString(
                        "ApiKey",
                        string.Empty)
                    .Trim();

            m_UploadCharge =
                Math.Max(
                    0,
                    economy.GetInt(
                        "UploadCharge",
                        0));

            m_GroupCreationCharge =
                Math.Max(
                    0,
                    economy.GetInt(
                        "GroupCreationCharge",
                        0));

            m_RequestTimeoutMilliseconds =
                Math.Clamp(
                    economy.GetInt(
                        "RequestTimeoutMilliseconds",
                        3000),
                    500,
                    30000);

            string feeWallet =
                economy.GetString(
                        "FeeWalletId",
                        string.Empty)
                    .Trim();

            if (!string.IsNullOrWhiteSpace(
                    feeWallet) &&
                !UUID.TryParse(
                    feeWallet,
                    out m_FeeWalletId))
            {
                throw new InvalidOperationException(
                    "[NEX-ECONOMY-VIEWER]: FeeWalletId must be a UUID.");
            }

            if (string.IsNullOrWhiteSpace(
                    m_WorldApiBaseUrl))
            {
                throw new InvalidOperationException(
                    "[NEX-ECONOMY-VIEWER]: WorldApiBaseUrl is required when enabled.");
            }

            if (!Uri.TryCreate(
                    m_WorldApiBaseUrl,
                    UriKind.Absolute,
                    out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp &&
                 baseUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "[NEX-ECONOMY-VIEWER]: WorldApiBaseUrl must be an absolute HTTP(S) URL.");
            }

            if (string.IsNullOrWhiteSpace(
                    m_ApiKey))
            {
                throw new InvalidOperationException(
                    "[NEX-ECONOMY-VIEWER]: ApiKey is required when enabled.");
            }

            m_Http =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromMilliseconds(
                            m_RequestTimeoutMilliseconds)
                };

            m_Enabled =
                true;

            m_Log.InfoFormat(
                "[NEX-ECONOMY-VIEWER]: Central NV$ Viewer adapter enabled for {0}.",
                m_WorldApiBaseUrl);
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(
            Scene scene)
        {
            if (!m_Enabled ||
                scene == null)
            {
                return;
            }

            lock (m_Sync)
                m_Scenes[scene.RegionInfo.RegionHandle] =
                    scene;

            scene.RegisterModuleInterface<IMoneyModule>(
                this);

            scene.EventManager.OnNewClient +=
                OnNewClient;
            scene.EventManager.OnMoneyTransfer +=
                MoneyTransferAction;
            scene.EventManager.OnClientClosed +=
                ClientClosed;
        }

        public void RemoveRegion(
            Scene scene)
        {
            if (scene == null)
                return;

            scene.EventManager.OnNewClient -=
                OnNewClient;
            scene.EventManager.OnMoneyTransfer -=
                MoneyTransferAction;
            scene.EventManager.OnClientClosed -=
                ClientClosed;

            lock (m_Sync)
                m_Scenes.Remove(
                    scene.RegionInfo.RegionHandle);
        }

        public void RegionLoaded(
            Scene scene)
        {
        }

        public void Close()
        {
            lock (m_Sync)
                m_Scenes.Clear();

            m_Http?.Dispose();
            m_Http =
                null;
        }

        public int GetBalance(
            UUID agentID)
        {
            if (!m_Enabled ||
                agentID.IsZero())
            {
                return 0;
            }

            try
            {
                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/economy/balance?account_id=" +
                        Uri.EscapeDataString(
                            agentID.ToString()));

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (!response.IsSuccessStatusCode)
                    return 0;

                string json =
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty(
                        "account",
                        out JsonElement account) ||
                    !account.TryGetProperty(
                        "balance",
                        out JsonElement balance) ||
                    !balance.TryGetInt64(
                        out long rawBalance))
                {
                    return 0;
                }

                return rawBalance > int.MaxValue
                    ? int.MaxValue
                    : rawBalance < int.MinValue
                        ? int.MinValue
                        : (int)rawBalance;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-ECONOMY-VIEWER]: Balance lookup failed for {0}: {1}",
                    agentID,
                    e.Message);
                return 0;
            }
        }

        public bool UploadCovered(
            UUID agentID,
            int amount) =>
            AmountCovered(
                agentID,
                amount);

        public bool AmountCovered(
            UUID agentID,
            int amount)
        {
            if (amount <= 0)
                return true;

            return
                GetBalance(agentID) >=
                amount;
        }

        public void ApplyCharge(
            UUID agentID,
            int amount,
            MoneyTransactionType type,
            string extraData = "")
        {
            ApplyFee(
                agentID,
                amount,
                string.IsNullOrWhiteSpace(extraData)
                    ? type.ToString()
                    : extraData);
        }

        public void ApplyUploadCharge(
            UUID agentID,
            int amount,
            string text)
        {
            ApplyFee(
                agentID,
                amount,
                string.IsNullOrWhiteSpace(text)
                    ? "Asset upload"
                    : text);
        }

        public void MoveMoney(
            UUID fromUser,
            UUID toUser,
            int amount,
            string text)
        {
            MoveMoneyInternal(
                fromUser,
                toUser,
                amount,
                text,
                "viewer-" +
                Guid.NewGuid().ToString("N"),
                out _);
        }

        public bool MoveMoney(
            UUID fromUser,
            UUID toUser,
            int amount,
            MoneyTransactionType type,
            string text)
        {
            string reference =
                string.IsNullOrWhiteSpace(text)
                    ? type.ToString()
                    : text;

            if (reference.StartsWith(
                    "Land purchase:",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CommercePaymentInternal(
                    fromUser,
                    toUser,
                    amount,
                    "LandPurchase",
                    reference,
                    "viewer-land-purchase",
                    Guid.NewGuid(),
                    out _);
            }

            return MoveMoneyInternal(
                fromUser,
                toUser,
                amount,
                reference,
                "viewer-" +
                Guid.NewGuid().ToString("N"),
                out _);
        }

        public bool ObjectGiveMoney(
            UUID objectID,
            UUID fromID,
            UUID toID,
            int amount,
            UUID txn,
            out string reason)
        {
            string idempotency =
                txn.IsZero()
                    ? "object-" +
                      Guid.NewGuid().ToString("N")
                    : "object-" +
                      txn.ToString();

            bool success =
                MoveMoneyInternal(
                    fromID,
                    toID,
                    amount,
                    "Object " +
                    objectID.ToString() +
                    " payment",
                    idempotency,
                    out reason);

            return success;
        }

        private void OnNewClient(
            IClientAPI client)
        {
            if (client == null)
                return;

            client.OnEconomyDataRequest +=
                EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest +=
                SendMoneyBalance;
            client.OnObjectBuy +=
                ObjectBuy;
        }


        private void ObjectBuy(
            IClientAPI remoteClient,
            UUID agentID,
            UUID sessionID,
            UUID groupID,
            UUID categoryID,
            uint localID,
            byte saleType,
            int salePrice)
        {
            if (remoteClient == null ||
                remoteClient.AgentId != agentID ||
                remoteClient.SessionId != sessionID ||
                remoteClient.Scene is not Scene scene)
            {
                return;
            }

            SceneObjectPart part =
                scene.GetSceneObjectPart(
                    localID);

            if (part == null ||
                part.ParentGroup == null ||
                part.ParentGroup.IsDeleted)
            {
                remoteClient.SendAgentAlertMessage(
                    "Unable to buy now. The object was not found.",
                    false);
                return;
            }

            SceneObjectGroup group =
                part.ParentGroup;
            SceneObjectPart root =
                group.RootPart;

            if (root.ObjectSaleType == (byte)SaleType.Not ||
                root.ObjectSaleType != saleType ||
                root.SalePrice != salePrice)
            {
                remoteClient.SendAgentAlertMessage(
                    "Object sale data changed. Please refresh the object properties and try again.",
                    false);
                return;
            }

            IBuySellModule buySell =
                scene.RequestModuleInterface<IBuySellModule>();

            if (buySell == null)
            {
                remoteClient.SendAgentAlertMessage(
                    "Object buying is unavailable in this region.",
                    false);
                return;
            }

            UUID seller =
                group.OwnerID;

            if (seller.IsZero() ||
                seller == agentID)
            {
                remoteClient.SendAgentAlertMessage(
                    "This object cannot be purchased from its current owner.",
                    false);
                return;
            }

            Guid orderId =
                Guid.NewGuid();

            string paymentError =
                string.Empty;

            bool paid =
                salePrice <= 0 ||
                CommercePaymentInternal(
                    agentID,
                    seller,
                    salePrice,
                    "ObjectSale",
                    "Object purchase: " +
                    root.Name,
                    "object:" +
                    group.UUID.ToString(),
                    orderId,
                    out paymentError);

            if (!paid)
            {
                remoteClient.SendAgentAlertMessage(
                    string.IsNullOrWhiteSpace(paymentError)
                        ? "NV$ object purchase payment failed."
                        : paymentError,
                    false);
                return;
            }

            bool delivered =
                buySell.BuyObject(
                    remoteClient,
                    categoryID,
                    localID,
                    saleType,
                    salePrice);

            if (!delivered)
            {
                bool refunded =
                    salePrice <= 0;

                string refundError =
                    string.Empty;

                if (salePrice > 0)
                {
                    refunded =
                        RefundCommerceInternal(
                            orderId,
                            "Object delivery failed",
                            out refundError);
                }

                if (!refunded)
                {
                    m_Log.ErrorFormat(
                        "[NEX-ECONOMY-VIEWER]: Object delivery failed and NexCommerce refund {0} also failed: {1}",
                        orderId,
                        refundError);
                }

                remoteClient.SendAgentAlertMessage(
                    refunded
                        ? "Object delivery failed. The completed NV$ payment was refunded."
                        : "Object delivery failed and the automatic NV$ refund could not be confirmed. Please contact an administrator with order " +
                          orderId.ToString("D") +
                          ".",
                    false);
                return;
            }

            SendBalanceRefresh(agentID);
            SendBalanceRefresh(seller);

            remoteClient.SendAgentAlertMessage(
                salePrice > 0
                    ? "Object purchased for " +
                      salePrice +
                      " NV$."
                    : "Object purchased.",
                false);
        }

        private void ClientClosed(
            UUID agentId,
            Scene scene)
        {
            // Client event subscriptions die with the client connection.
            // Keep this handler only to match the EventManager contract and
            // avoid retaining per-client state in this module.
        }

        private void EconomyDataRequestHandler(
            IClientAPI client)
        {
            if (client?.Scene is not Scene scene)
                return;

            client.SendEconomyData(
                1f,
                scene.RegionInfo.ObjectCapacity,
                0,
                0,
                GroupCreationCharge,
                0,
                0,
                10f,
                0,
                1f,
                0,
                0,
                0,
                0,
                UploadCharge,
                0,
                2f);
        }

        private void SendMoneyBalance(
            IClientAPI client,
            UUID agentID,
            UUID sessionID,
            UUID transactionID)
        {
            if (client == null ||
                client.AgentId != agentID ||
                client.SessionId != sessionID)
            {
                return;
            }

            int balance =
                GetBalance(agentID);

            client.SendMoneyBalance(
                transactionID,
                true,
                Array.Empty<byte>(),
                balance,
                0,
                UUID.Zero,
                false,
                UUID.Zero,
                false,
                0,
                string.Empty);
        }

        private void MoneyTransferAction(
            object sender,
            EventManager.MoneyTransferArgs e)
        {
            if (e == null ||
                e.amount <= 0)
            {
                return;
            }

            UUID paymentReceiver =
                e.receiver;
            bool objectPayment =
                TryResolveLocalObjectOwner(
                    e.receiver,
                    out UUID objectOwner);

            if (objectPayment)
                paymentReceiver = objectOwner;

            Guid commerceOrderId =
                Guid.NewGuid();

            string reason;
            bool success;

            if (objectPayment)
            {
                success =
                    CommercePaymentInternal(
                        e.sender,
                        paymentReceiver,
                        e.amount,
                        "VendorPayment",
                        string.IsNullOrWhiteSpace(e.description)
                            ? "Inworld object payment"
                            : e.description,
                        "object:" +
                        e.receiver.ToString(),
                        commerceOrderId,
                        out reason);
            }
            else
            {
                success =
                    MoveMoneyInternal(
                        e.sender,
                        paymentReceiver,
                        e.amount,
                        e.description,
                        "viewer-event-" +
                        Guid.NewGuid().ToString("N"),
                        out reason);
            }

            if (!success)
            {
                IClientAPI client =
                    LocateClient(
                        e.sender);

                client?.SendAgentAlertMessage(
                    string.IsNullOrWhiteSpace(reason)
                        ? "NV$ transfer failed."
                        : reason,
                    false);

                return;
            }

            SendBalanceRefresh(
                e.sender);
            SendBalanceRefresh(
                paymentReceiver);

            LocateClient(
                e.sender)
                ?.SendAgentAlertMessage(
                    "NV$ transfer completed: " +
                    e.amount +
                    " NV$.",
                    false);

            IClientAPI receiverClient =
                LocateClient(
                    paymentReceiver);

            if (receiverClient != null &&
                paymentReceiver != e.sender)
            {
                receiverClient.SendAgentAlertMessage(
                    "NV$ received: " +
                    e.amount +
                    " NV$.",
                    false);
            }

            if (objectPayment)
            {
                try
                {
                    OnObjectPaid?.Invoke(
                        e.receiver,
                        e.sender,
                        e.amount);
                }
                catch (Exception ex)
                {
                    m_Log.WarnFormat(
                        "[NEX-ECONOMY-VIEWER]: ObjectPaid subscriber failed: {0}",
                        ex.Message);
                }
            }
        }

        private void ApplyFee(
            UUID agentID,
            int amount,
            string reference)
        {
            if (amount <= 0)
                return;

            if (m_FeeWalletId.IsZero())
            {
                throw new InvalidOperationException(
                    "NV$ fee wallet is not configured.");
            }

            if (!MoveMoneyInternal(
                    agentID,
                    m_FeeWalletId,
                    amount,
                    reference,
                    "fee-" +
                    Guid.NewGuid().ToString("N"),
                    out string reason))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(reason)
                        ? "NV$ fee transfer failed."
                        : reason);
            }
        }


        private bool CommercePaymentInternal(
            UUID buyer,
            UUID seller,
            int amount,
            string kind,
            string reference,
            string externalReference,
            Guid orderId,
            out string reason)
        {
            reason =
                string.Empty;

            if (!m_Enabled ||
                buyer.IsZero() ||
                seller.IsZero() ||
                buyer == seller ||
                amount <= 0 ||
                orderId == Guid.Empty)
            {
                reason =
                    "Invalid NV$ commerce payment.";
                return false;
            }

            try
            {
                if (!EnsureLocalServiceWallet(
                        buyer,
                        out reason) ||
                    !EnsureLocalServiceWallet(
                        seller,
                        out reason))
                {
                    return false;
                }

                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            order_id =
                                orderId.ToString(),
                            kind,
                            buyer_account_id =
                                buyer.ToString(),
                            seller_account_id =
                                seller.ToString(),
                            amount,
                            reference =
                                string.IsNullOrWhiteSpace(reference)
                                    ? "NexCommerce payment"
                                    : reference,
                            external_reference =
                                externalReference ??
                                string.Empty
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/commerce/orders");

                request.Content =
                    new ByteArrayContent(
                        payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (response.IsSuccessStatusCode)
                    return true;

                reason =
                    ReadErrorMessage(
                        response);
                return false;
            }
            catch (Exception e)
            {
                reason =
                    "NexCommerce service unavailable.";

                m_Log.WarnFormat(
                    "[NEX-ECONOMY-VIEWER]: Commerce payment {0}->{1} failed: {2}",
                    buyer,
                    seller,
                    e.Message);
                return false;
            }
        }

        private bool RefundCommerceInternal(
            Guid orderId,
            string reasonText,
            out string reason)
        {
            reason =
                string.Empty;

            try
            {
                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            reason =
                                string.IsNullOrWhiteSpace(reasonText)
                                    ? "Commerce rollback"
                                    : reasonText
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/commerce/orders/" +
                        orderId.ToString("D") +
                        "/refund");

                request.Content =
                    new ByteArrayContent(
                        payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (response.IsSuccessStatusCode)
                    return true;

                reason =
                    ReadErrorMessage(
                        response);
                return false;
            }
            catch (Exception e)
            {
                reason =
                    "NexCommerce refund service unavailable.";

                m_Log.WarnFormat(
                    "[NEX-ECONOMY-VIEWER]: Commerce refund {0} failed: {1}",
                    orderId,
                    e.Message);
                return false;
            }
        }

        private bool MoveMoneyInternal(
            UUID fromUser,
            UUID toUser,
            int amount,
            string reference,
            string idempotencyKey,
            out string reason)
        {
            reason =
                string.Empty;

            if (!m_Enabled ||
                fromUser.IsZero() ||
                toUser.IsZero() ||
                fromUser == toUser ||
                amount <= 0)
            {
                reason =
                    "Invalid NV$ transfer.";
                return false;
            }

            try
            {
                if (!EnsureLocalServiceWallet(
                        fromUser,
                        out reason) ||
                    !EnsureLocalServiceWallet(
                        toUser,
                        out reason))
                {
                    return false;
                }

                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            from_account_id =
                                fromUser.ToString(),
                            to_account_id =
                                toUser.ToString(),
                            amount,
                            reference =
                                string.IsNullOrWhiteSpace(reference)
                                    ? "Viewer transfer"
                                    : reference
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/economy/transfers");

                request.Headers.TryAddWithoutValidation(
                    "Idempotency-Key",
                    idempotencyKey);

                request.Content =
                    new ByteArrayContent(
                        payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (response.IsSuccessStatusCode)
                    return true;

                reason =
                    ReadErrorMessage(
                        response);

                return false;
            }
            catch (Exception e)
            {
                reason =
                    "NV$ service unavailable.";

                m_Log.WarnFormat(
                    "[NEX-ECONOMY-VIEWER]: Transfer {0}->{1} failed: {2}",
                    fromUser,
                    toUser,
                    e.Message);

                return false;
            }
        }

        private HttpRequestMessage CreateRequest(
            HttpMethod method,
            string path)
        {
            HttpRequestMessage request =
                new HttpRequestMessage(
                    method,
                    m_WorldApiBaseUrl +
                    path);

            request.Headers.TryAddWithoutValidation(
                "X-NexVerse-Api-Key",
                m_ApiKey);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            return request;
        }

        private static string ReadErrorMessage(
            HttpResponseMessage response)
        {
            try
            {
                string json =
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                using JsonDocument document =
                    JsonDocument.Parse(
                        json);

                if (document.RootElement.TryGetProperty(
                        "message",
                        out JsonElement message) &&
                    message.ValueKind ==
                        JsonValueKind.String)
                {
                    return
                        message.GetString() ??
                        "NV$ transfer failed.";
                }
            }
            catch
            {
            }

            return
                "NV$ transfer failed (" +
                (int)response.StatusCode +
                ").";
        }

        private IClientAPI LocateClient(
            UUID agentId)
        {
            lock (m_Sync)
            {
                foreach (Scene scene in
                         m_Scenes.Values)
                {
                    ScenePresence presence =
                        scene.GetScenePresence(
                            agentId);

                    if (presence?.ControllingClient !=
                        null)
                    {
                        return
                            presence.ControllingClient;
                    }
                }
            }

            return null;
        }

        private void SendBalanceRefresh(
            UUID agentId)
        {
            IClientAPI client =
                LocateClient(
                    agentId);

            if (client == null)
                return;

            client.SendMoneyBalance(
                UUID.Random(),
                true,
                Array.Empty<byte>(),
                GetBalance(agentId),
                0,
                UUID.Zero,
                false,
                UUID.Zero,
                false,
                0,
                string.Empty);
        }



        private bool EnsureLocalServiceWallet(
            UUID accountId,
            out string reason)
        {
            reason =
                string.Empty;

            if (!TryResolveLocalGroup(
                    accountId,
                    out string groupName))
            {
                return true;
            }

            try
            {
                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            account_id =
                                accountId.ToString(),
                            account_class =
                                "group",
                            display_name =
                                string.IsNullOrWhiteSpace(groupName)
                                    ? accountId.ToString()
                                    : groupName
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/economy/accounts/ensure");

                request.Content =
                    new ByteArrayContent(
                        payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (response.IsSuccessStatusCode)
                    return true;

                reason =
                    ReadErrorMessage(
                        response);

                return false;
            }
            catch (Exception e)
            {
                reason =
                    "NV$ group wallet provisioning failed.";

                m_Log.WarnFormat(
                    "[NEX-ECONOMY-VIEWER]: Group wallet {0} provisioning failed: {1}",
                    accountId,
                    e.Message);

                return false;
            }
        }

        private bool TryResolveLocalGroup(
            UUID groupId,
            out string groupName)
        {
            groupName =
                string.Empty;

            if (groupId.IsZero())
                return false;

            lock (m_Sync)
            {
                foreach (Scene scene in
                         m_Scenes.Values)
                {
                    IGroupsModule groups =
                        scene.RequestModuleInterface<IGroupsModule>();

                    GroupRecord record =
                        groups?.GetGroupRecord(
                            groupId);

                    if (record == null)
                        continue;

                    groupName =
                        record.GroupName ??
                        string.Empty;

                    return true;
                }
            }

            return false;
        }

        private bool TryResolveLocalObjectOwner(
            UUID objectId,
            out UUID ownerId)
        {
            ownerId =
                UUID.Zero;

            lock (m_Sync)
            {
                foreach (Scene scene in
                         m_Scenes.Values)
                {
                    SceneObjectPart part =
                        scene.GetSceneObjectPart(
                            objectId);

                    if (part?.ParentGroup == null ||
                        part.ParentGroup.IsDeleted)
                    {
                        continue;
                    }

                    ownerId =
                        part.ParentGroup.OwnerID;

                    return !ownerId.IsZero();
                }
            }

            return false;
        }

        private bool IsLocalObject(
            UUID objectId)
        {
            lock (m_Sync)
            {
                foreach (Scene scene in
                         m_Scenes.Values)
                {
                    if (scene.GetSceneObjectPart(
                            objectId) != null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
