﻿using System.Diagnostics;
using System.Reactive.Linq;
using Angor.Shared.Models;
using Nostr.Client.Requests;
using Microsoft.Extensions.Logging;
using Nostr.Client.Client;
using Nostr.Client.Communicator;
using Nostr.Client.Keys;
using Nostr.Client.Messages;
using Nostr.Client.Responses;

namespace Angor.Shared.Services
{
    public class RelayService : IRelayService
    {
        private static NostrWebsocketClient? _nostrClient;
        private static INostrCommunicator? _nostrCommunicator;
        
        private ILogger<RelayService> _logger;

        private ILogger<NostrWebsocketClient> _clientLogger; 
        private ILogger<NostrWebsocketCommunicator> _communicatorLogger;

        private Dictionary<string, IDisposable> subscriptions = new();
        private Dictionary<string, Action<NostrOkResponse>> OkVerificationActions = new();

        public RelayService(
            ILogger<RelayService> logger, 
            ILogger<NostrWebsocketClient> clientLogger, 
            ILogger<NostrWebsocketCommunicator> communicatorLogger)
        {
            _logger = logger;
            _clientLogger = clientLogger;
            _communicatorLogger = communicatorLogger;
        }

        public async Task ConnectToRelaysAsync()
        {
            if (_nostrCommunicator == null)
            {
                SetupNostrCommunicator();
            }
            
            await _nostrCommunicator.StartOrFail();

            if (_nostrClient != null)
                return;
            SetupNostrClient();
        }

        public void RegisterOKMessageHandler(string eventId, Action<NostrOkResponse> action)
        {
            OkVerificationActions.Add(eventId,action);
        }

        public Task RequestProjectDataAsync<T>(Action<T> responseDataAction,params string[] nostrPubKeys)
        {
            string subscriptionName = "ProjectInfoLookups";
            _nostrClient.Send(new NostrRequest(subscriptionName, new NostrFilter
            {
                Authors = nostrPubKeys,
                Kinds = new[] { NostrKind.ApplicationSpecificData, NostrKind.Metadata, (NostrKind)30402 },
            }));

            if (!subscriptions.ContainsKey(subscriptionName))
            {
                var subscription = _nostrClient.Streams.EventStream
                    .Where(_ => _.Subscription == subscriptionName)
                    .Where(_ => nostrPubKeys.Contains(_.Event.Pubkey))
                    .Select(_ => _.Event)
                    .Subscribe(ev =>
                    {
                        responseDataAction(Newtonsoft.Json.JsonConvert.DeserializeObject<T>(ev.Content));
                    });

                subscriptions.Add(subscriptionName, subscription);
            }

            return Task.CompletedTask;
        }

        public void CloseConnection()
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Dispose();
            }
            _nostrClient?.Dispose();
            _nostrCommunicator?.Dispose();
        }

        public Task<string> AddProjectAsync(ProjectInfo project, string hexPrivateKey)
        {
            var content = Newtonsoft.Json.JsonConvert.SerializeObject(project);

            var key = NostrPrivateKey.FromHex(hexPrivateKey);
            
            var signed = GetNip78NostrEvent(project, content)
                .Sign(key);

            if (_nostrClient == null)
                throw new InvalidOperationException();
            
            _nostrClient.Send(new NostrEventRequest(signed));
            
            return Task.FromResult(signed.Id);
        }

        private static NostrEvent GetNip78NostrEvent(ProjectInfo project, string content)
        {
            var ev = new NostrEvent
            {
                Kind = NostrKind.ApplicationSpecificData,
                CreatedAt = DateTime.UtcNow,
                Content = content,
                Pubkey = project.NostrPubKey,
                Tags = new NostrEventTags( //TODO need to find the correct tags for the event
                    new NostrEventTag("d", "AngorApp", "Create a new project event"),
                    new NostrEventTag("L", "#projectInfo"),
                    new NostrEventTag("l", "ProjectDeclaration", "#projectInfo"))
            };
            return ev;
        }
        
        private static NostrEvent GetNip99NostrEvent(ProjectInfo project, string content)
        {
            var ev = new NostrEvent
            {
                Kind = (NostrKind)30402,
                CreatedAt = DateTime.UtcNow,
                Content = content,
                Pubkey = project.NostrPubKey,
                Tags = new NostrEventTags( //TODO need to find the correct tags for the event
                    new NostrEventTag("d", "AngorApp", "Create a new project event"),
                    new NostrEventTag("title", "New project :)"),
                    new NostrEventTag("published_at", DateTime.UtcNow.ToString()),
                    new NostrEventTag("t","#AngorProjectInfo"),
                    new NostrEventTag("image",""),
                    new NostrEventTag("summary","A new project that will save the world"),
                    new NostrEventTag("location",""),
                    new NostrEventTag("price","1","BTC"))
            };
            
            return ev;
        }
        
        
        private void SetupNostrClient()
        {
            _nostrClient = new NostrWebsocketClient(_nostrCommunicator, _clientLogger);
            
            _nostrClient.Streams.UnknownMessageStream.Subscribe(_ => _clientLogger.LogError($"UnknownMessageStream {_.MessageType} {_.AdditionalData}"));
            _nostrClient.Streams.EventStream.Subscribe(_ => _clientLogger.LogInformation($"EventStream {_.Subscription} {_.AdditionalData}"));
            _nostrClient.Streams.NoticeStream.Subscribe(_ => _clientLogger.LogError($"NoticeStream {_.Message}"));
            _nostrClient.Streams.UnknownRawStream.Subscribe(_ => _clientLogger.LogError($"UnknownRawStream {_.Message}"));
            
            _nostrClient.Streams.OkStream.Subscribe(_ =>
            {
                _clientLogger.LogInformation($"OkStream {_.Accepted} message - {_.Message}");

                if (_.EventId != null && OkVerificationActions.ContainsKey(_.EventId))
                {
                    OkVerificationActions[_.EventId](_);
                    OkVerificationActions.Remove(_.EventId);
                }
            });

            _nostrClient.Streams.EoseStream.Subscribe(_ =>
            {
                _clientLogger.LogInformation($"EoseStream {_.Subscription} message - {_.AdditionalData}");
                
                if (!subscriptions.ContainsKey(_.Subscription))
                    return;
                
                _clientLogger.LogInformation($"Disposing of subscription - {_.Subscription}");
                subscriptions[_.Subscription].Dispose();
                subscriptions.Remove(_.Subscription);
                _clientLogger.LogInformation($"subscription disposed - {_.Subscription}");
            });
        }

        private void SetupNostrCommunicator()
        {
            _nostrCommunicator = new NostrWebsocketCommunicator(new Uri("ws://angor-relay.test"))
            {
                Name = "angor-relay.test",
                ReconnectTimeout = null //TODO need to check what is the actual best time to set here
            };

            _nostrCommunicator.DisconnectionHappened.Subscribe(info =>
            {
                if (info.Exception != null)
                    _communicatorLogger.LogError(info.Exception,
                        "Relay disconnected, type: {Type}, reason: {CloseStatus}", info.Type,
                        info.CloseStatusDescription);
                else
                    _communicatorLogger.LogInformation("Relay disconnected, type: {Type}, reason: {CloseStatus}",
                        info.Type, info.CloseStatusDescription);
            });

            _nostrCommunicator.MessageReceived.Subscribe(info =>
            {
                _communicatorLogger.LogInformation(
                    "message received on communicator - {Text} Relay message received, type: {MessageType}",
                    info.Text, info.MessageType);
            });
        }
    }

}