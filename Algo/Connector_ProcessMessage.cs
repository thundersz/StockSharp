namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;

	using Ecng.Collections;
	using Ecng.Common;

	using StockSharp.Algo.Risk;
	using StockSharp.Algo.Storages;
	using StockSharp.BusinessEntities;
	using StockSharp.Localization;
	using StockSharp.Logging;
	using StockSharp.Messages;

	partial class Connector
	{
		private readonly SyncObject _marketTimerSync = new();
		private Timer _marketTimer;
		private readonly TimeMessage _marketTimeMessage = new();
		private bool _isMarketTimeHandled;

		private void CreateTimer()
		{
			lock (_marketTimerSync)
			{
				_isMarketTimeHandled = true;

				if (_marketTimer != null)
					return;

				_marketTimer = ThreadingHelper
					.Timer(() =>
					{
						try
						{
							// TimeMsg required for notify invoke MarketTimeChanged event (and active time based IMarketRule-s)
							// No need to put _marketTimeMessage again, if it still in queue.

							lock (_marketTimerSync)
							{
								if (_marketTimer == null || !_isMarketTimeHandled)
									return;

								_isMarketTimeHandled = false;
							}

							_marketTimeMessage.LocalTime = TimeHelper.NowWithOffset;
							SendOutMessage(_marketTimeMessage);
						}
						catch (Exception ex)
						{
							ex.LogError();
						}
					})
					.Interval(MarketTimeChangedInterval);
			}
		}

		private void CloseTimer()
		{
			lock (_marketTimerSync)
			{
				if (_marketTimer != null)
				{
					_marketTimer.Dispose();
					_marketTimer = null;
				}

				_isMarketTimeHandled = false;
			}
		}

		private readonly ResetMessage _disposeMessage = new();

		private void AdapterOnNewOutMessage(Message message)
		{
			if (message.IsBack())
			{
				//message.IsBack = false;

				// lookup messages now sends in BasketMessageAdapter
				// nested subscription ignores by Connector
				//
				//if (message.Type == MessageTypes.MarketData)
				//{
				//	var mdMsg = (MarketDataMessage)message;

				//	var security = !mdMsg.DataType.IsSecurityRequired() ? null : GetSecurity(mdMsg.SecurityId);
				//	_subscriptionManager.ProcessRequest(security, mdMsg, true);
				//}
				//else

				if (message.Type == MessageTypes.OrderGroupCancel)
				{
					var cancelMsg = (OrderGroupCancelMessage)message;
					// offline (back) and risk managers can generate the message
					_entityCache.TryAddMassCancelationId(cancelMsg.TransactionId);
				}
				else if (message.Type == ExtendedMessageTypes.SubscriptionSecurityAll)
				{
					_subscriptionManager.SubscribeAll((SubscriptionSecurityAllMessage)message);
					return;
				}

				SendInMessage(message);
			}
			else
				SendOutMessage(message);
		}

		private IMessageChannel _inMessageChannel;

		/// <summary>
		/// Input message channel.
		/// </summary>
		public IMessageChannel InMessageChannel
		{
			get => _inMessageChannel;
			protected set
			{
				if (value == _inMessageChannel)
					return;

				if (_inMessageChannel != null)
				{
					_inMessageChannel.NewOutMessage -= InMessageChannelOnNewOutMessage;
					_inMessageChannel?.Dispose();
				}

				_inMessageChannel = value;

				if (_inMessageChannel != null)
					_inMessageChannel.NewOutMessage += InMessageChannelOnNewOutMessage;
			}
		}

		private IMessageChannel _outMessageChannel;

		/// <summary>
		/// Outgoing message channel.
		/// </summary>
		public IMessageChannel OutMessageChannel
		{
			get => _outMessageChannel;
			protected set
			{
				if (value == _outMessageChannel)
					return;

				if (_outMessageChannel != null)
				{
					_outMessageChannel.NewOutMessage -= OutMessageChannelOnNewOutMessage;
					_outMessageChannel?.Dispose();
				}

				_outMessageChannel = value;

				if (_outMessageChannel != null)
					_outMessageChannel.NewOutMessage += OutMessageChannelOnNewOutMessage;
			}
		}

		private void InMessageChannelOnNewOutMessage(Message message)
		{
			_inAdapter?.SendInMessage(message);

			if (message != _disposeMessage)
				return;

			InMessageChannel = null;
			Adapter = null;
			OutMessageChannel = null;
		}

		private void OutMessageChannelOnNewOutMessage(Message message)
		{
			OnProcessMessage(message);
		}

		private IMessageAdapter _inAdapter;

		/// <summary>
		/// Inner message adapter.
		/// </summary>
		public IMessageAdapter InnerAdapter
		{
			get => _inAdapter;
			set
			{
				if (_inAdapter == value)
					return;

				if (_inAdapter != null)
				{
					_inAdapter.NewOutMessage -= AdapterOnNewOutMessage;
				}

				if (_adapter != null)
				{
					_adapter.InnerAdapters.Added -= InnerAdaptersOnAdded;
					_adapter.InnerAdapters.Removed -= InnerAdaptersOnRemoved;
					_adapter.InnerAdapters.Cleared -= InnerAdaptersOnCleared;
				}

				_inAdapter = value;
				_adapter = null;
				StorageAdapter = null;

				if (_inAdapter == null)
					return;

				var adapter = _inAdapter as IMessageAdapterWrapper;

				while (adapter != null)
				{
					if (adapter is StorageMetaInfoMessageAdapter storage)
						StorageAdapter = storage;

					if (adapter.InnerAdapter is BasketMessageAdapter basket)
						_adapter = basket;

					adapter = adapter.InnerAdapter as IMessageAdapterWrapper;
				}

				if (_adapter != null)
				{
					_adapter.InnerAdapters.Added += InnerAdaptersOnAdded;
					_adapter.InnerAdapters.Removed += InnerAdaptersOnRemoved;
					_adapter.InnerAdapters.Cleared += InnerAdaptersOnCleared;

					foreach (var inner in _adapter.InnerAdapters)
						InnerAdaptersOnAdded(inner);
				}

				_inAdapter.NewOutMessage += AdapterOnNewOutMessage;
			}
		}

		private BasketMessageAdapter _adapter;

		/// <summary>
		/// Message adapter.
		/// </summary>
		public BasketMessageAdapter Adapter
		{
			get => _adapter;
			protected set
			{
				if (!_isDisposing && value == null)
					throw new ArgumentNullException(nameof(value));

				if (_adapter == value)
					return;

				if (_adapter != null)
				{
					_adapter.InnerAdapters.Added -= InnerAdaptersOnAdded;
					_adapter.InnerAdapters.Removed -= InnerAdaptersOnRemoved;
					_adapter.InnerAdapters.Cleared -= InnerAdaptersOnCleared;

					//SendInMessage(new ResetMessage());

					_inAdapter.NewOutMessage -= AdapterOnNewOutMessage;
					_inAdapter.Dispose();

					//if (_inAdapter != _adapter)
					//	_adapter.Dispose();
				}

				_adapter = value;
				_inAdapter = _adapter;

				if (_adapter != null)
				{
					_adapter.InnerAdapters.Added += InnerAdaptersOnAdded;
					_adapter.InnerAdapters.Removed += InnerAdaptersOnRemoved;
					_adapter.InnerAdapters.Cleared += InnerAdaptersOnCleared;

					_adapter.Parent = this;

					//_inAdapter = new ChannelMessageAdapter(_inAdapter, InMessageChannel, OutMessageChannel)
					//{
					//	//OwnOutputChannel = true,
					//	OwnInnerAdapter = true
					//};

					if (RiskManager != null)
						_inAdapter = new RiskMessageAdapter(_inAdapter) { RiskManager = RiskManager, OwnInnerAdapter = true };

					if (SecurityStorage != null && StorageRegistry != null)
					{
						_inAdapter = StorageAdapter = new StorageMetaInfoMessageAdapter(_inAdapter, SecurityStorage, PositionStorage, StorageRegistry.ExchangeInfoProvider, _adapter.StorageProcessor)
						{
							OwnInnerAdapter = true,
							OverrideSecurityData = OverrideSecurityData
						};
					}

					if (Buffer != null)
						_inAdapter = new BufferMessageAdapter(_inAdapter, _adapter.StorageSettings, Buffer, SnapshotRegistry);

					if (SupportBasketSecurities)
						_inAdapter = new BasketSecurityMessageAdapter(_inAdapter, this, BasketSecurityProcessorProvider, ExchangeInfoProvider) { OwnInnerAdapter = true };

					if (SupportSnapshots)
						_inAdapter = new SnapshotHolderMessageAdapter(_inAdapter, _entityCache) { OwnInnerAdapter = true };

					if (SupportAssociatedSecurity)
						_inAdapter = new AssociatedSecurityAdapter(_inAdapter) { OwnInnerAdapter = true };

					if (SupportFilteredMarketDepth)
						_inAdapter = new FilteredMarketDepthAdapter(_inAdapter) { OwnInnerAdapter = true };

					_inAdapter.NewOutMessage += AdapterOnNewOutMessage;
				}
			}
		}

		/// <summary>
		/// Use <see cref="BasketSecurityMessageAdapter"/>.
		/// </summary>
		public virtual bool SupportBasketSecurities => false;

		private bool _supportFilteredMarketDepth;

		/// <summary>
		/// Use <see cref="FilteredMarketDepthAdapter"/>.
		/// </summary>
		public bool SupportFilteredMarketDepth
		{
			get => _supportFilteredMarketDepth;
			set
			{
				if (_supportFilteredMarketDepth == value)
					return;

				if (value)
					EnableAdapter(a => new FilteredMarketDepthAdapter(a) { OwnInnerAdapter = true }, typeof(AssociatedSecurityAdapter));
				else
					DisableAdapter<FilteredMarketDepthAdapter>();

				_supportFilteredMarketDepth = value;
			}
		}

		private bool _supportSnapshots = true;

		/// <summary>
		/// Use <see cref="SnapshotHolderMessageAdapter"/>.
		/// </summary>
		public virtual bool SupportSnapshots
		{
			get => _supportSnapshots;
			set
			{
				if (_supportSnapshots == value)
					return;

				if (value)
					EnableAdapter(a => new SnapshotHolderMessageAdapter(a, _entityCache) { OwnInnerAdapter = true }, typeof(BasketSecurityMessageAdapter));
				else
					DisableAdapter<SnapshotHolderMessageAdapter>();

				_supportSnapshots = value;
			}
		}

		private bool _supportAssociatedSecurity;

		/// <summary>
		/// Use <see cref="AssociatedSecurityAdapter"/>.
		/// </summary>
		public bool SupportAssociatedSecurity
		{
			get => _supportAssociatedSecurity;
			set
			{
				if (_supportAssociatedSecurity == value)
					return;

				if (value)
					EnableAdapter(a => new AssociatedSecurityAdapter(a) { OwnInnerAdapter = true }, typeof(SnapshotHolderMessageAdapter));
				else
					DisableAdapter<AssociatedSecurityAdapter>();

				_supportAssociatedSecurity = value;
			}
		}

		/// <summary>
		/// Use <see cref="Level1DepthBuilderAdapter"/>.
		/// </summary>
		[Obsolete("Use IMessageAdapter.GenerateOrderBookFromLevel1 property.")]
		public bool SupportLevel1DepthBuilder { get; set; }

		/// <summary>
		/// Storage buffer.
		/// </summary>
		public StorageBuffer Buffer { get; }

		private Tuple<IMessageAdapter, IMessageAdapter, IMessageAdapter> GetAdapter(Type type)
		{
			var adapter = _inAdapter;

			if (adapter == null)
				return null;

			var prev = (adapter as IMessageAdapterWrapper)?.InnerAdapter;
			var next = (IMessageAdapter)null;

			while (true)
			{
				if (adapter.GetType() == type)
					return Tuple.Create(prev, adapter, next);

				next = adapter;
				adapter = prev;

				if (adapter == null)
					return null;

				prev = (adapter as IMessageAdapterWrapper)?.InnerAdapter;
			}
		}

		private Tuple<IMessageAdapter, IMessageAdapter, IMessageAdapter> GetAdapter<T>()
			where T : IMessageAdapterWrapper
		{
			return GetAdapter(typeof(T));
		}

		private void EnableAdapter(Func<IMessageAdapter, IMessageAdapterWrapper> create, Type type)
		{
			if (_inAdapter == null)
				return;

			var tuple = type != null ? GetAdapter(type) : null;
			var adapter = tuple?.Item2;

			if (adapter != null)
			{
				//if (after)
				//{
				if (tuple.Item3 is IMessageAdapterWrapper nextWrapper)
					nextWrapper.InnerAdapter = create(adapter);
				else
					AddAdapter(create);
				//}
				//else
				//{
				//	var prevWrapper = tuple.Item1;
				//	var nextWrapper = adapter as IMessageAdapterWrapper;

				//	if (prevWrapper == null)
				//		throw new InvalidOperationException("Adapter wrapper cannot be added to the beginning of the chain.");

				//	if (nextWrapper == null)
				//		throw new InvalidOperationException(LocalizedStrings.TypeNotImplemented.Put(adapter.GetType(), nameof(IMessageAdapterWrapper)));

				//	nextWrapper.InnerAdapter = create(prevWrapper);
				//}
			}
			else
				AddAdapter(create);
		}

		private void AddAdapter(Func<IMessageAdapter, IMessageAdapterWrapper> create)
		{
			_inAdapter.NewOutMessage -= AdapterOnNewOutMessage;

			_inAdapter = create(_inAdapter);
			_inAdapter.NewOutMessage += AdapterOnNewOutMessage;
		}

		private void DisableAdapter<T>()
			where T : IMessageAdapterWrapper
		{
			var tuple = GetAdapter<T>();

			if (tuple == null)
				return;

			var adapter = tuple.Item2;
			var adapterWrapper = (MessageAdapterWrapper)adapter;

			var next = tuple.Item3;
			var nextWrapper = (MessageAdapterWrapper)next;

			if (next == null)
			{
				adapterWrapper.NewOutMessage -= AdapterOnNewOutMessage;

				_inAdapter = adapterWrapper.InnerAdapter;
				_inAdapter.NewOutMessage += AdapterOnNewOutMessage;
			}
			else
				nextWrapper.InnerAdapter = adapterWrapper.InnerAdapter;

			adapterWrapper.OwnInnerAdapter = false;
			adapterWrapper.Dispose();
		}

		private void InnerAdaptersOnAdded(IMessageAdapter adapter)
		{
			if (adapter.IsTransactional())
				TransactionAdapter = adapter;

			if (adapter.IsMarketData())
				MarketDataAdapter = adapter;
		}

		private void InnerAdaptersOnRemoved(IMessageAdapter adapter)
		{
			if (TransactionAdapter == adapter)
				TransactionAdapter = null;

			if (MarketDataAdapter == adapter)
				MarketDataAdapter = null;
		}

		private void InnerAdaptersOnCleared()
		{
			TransactionAdapter = null;
			MarketDataAdapter = null;
		}

		/// <inheritdoc />
		public IMessageAdapter TransactionAdapter { get; private set; }

		/// <inheritdoc />
		public IMessageAdapter MarketDataAdapter { get; private set; }

		/// <summary>
		/// Storage adapter.
		/// </summary>
		public StorageMetaInfoMessageAdapter StorageAdapter { get; private set; }

		/// <inheritdoc />
		public bool SendInMessage(Message message)
		{
			message.TryInitLocalTime(this);

			if (!InMessageChannel.IsOpened())
				InMessageChannel.Open();

			return InMessageChannel.SendInMessage(message);
		}

		/// <inheritdoc />
		public void SendOutMessage(Message message)
		{
			message.TryInitLocalTime(this);

			if (!OutMessageChannel.IsOpened())
				OutMessageChannel.Open();

			OutMessageChannel.SendInMessage(message);
		}

		/// <summary>
		/// Send error message.
		/// </summary>
		/// <param name="error">Error details.</param>
		public void SendOutError(Exception error)
		{
			SendOutMessage(error.ToErrorMessage());
		}

		/// <summary>
		/// Process message.
		/// </summary>
		/// <param name="message">Message.</param>
		protected virtual void OnProcessMessage(Message message)
		{
			if (message.Type is not MessageTypes.Time and not MessageTypes.QuoteChange)
				this.AddVerboseLog("BP:{0}", message);

			ProcessTimeInterval(message);

			RaiseNewMessage(message);

			try
			{
				switch (message.Type)
				{
					case MessageTypes.Connect:
						ProcessConnectMessage((ConnectMessage)message);
						break;

					case MessageTypes.Disconnect:
						ProcessDisconnectMessage((DisconnectMessage)message);
						break;

					case ExtendedMessageTypes.ReconnectingStarted:
						ProcessReconnectingStartedMessage(message);
						break;

					case ExtendedMessageTypes.ReconnectingFinished:
						ProcessReconnectingFinishedMessage(message);
						break;

					case MessageTypes.QuoteChange:
						ProcessQuotesMessage((QuoteChangeMessage)message);
						break;

					case MessageTypes.Board:
						ProcessBoardMessage((BoardMessage)message);
						break;

					case MessageTypes.BoardState:
						ProcessBoardStateMessage((BoardStateMessage)message);
						break;

					case MessageTypes.Security:
						ProcessSecurityMessage((SecurityMessage)message);
						break;

					case MessageTypes.TimeFrameInfo:
						ProcessTimeFrameInfoMessage((TimeFrameInfoMessage)message);
						break;

					case MessageTypes.Level1Change:
						ProcessLevel1ChangeMessage((Level1ChangeMessage)message);
						break;

					case MessageTypes.News:
						ProcessNewsMessage((NewsMessage)message);
						break;

					case MessageTypes.Execution:
						ProcessExecutionMessage((ExecutionMessage)message);
						break;

					case MessageTypes.Portfolio:
						ProcessPortfolioMessage((PortfolioMessage)message);
						break;

					case MessageTypes.PositionChange:
						ProcessPositionChangeMessage((PositionChangeMessage)message);
						break;

					//case MessageTypes.Time:
					//	var timeMsg = (TimeMessage)message;

					//	if (timeMsg.Shift != null)
					//		TimeShift = timeMsg.Shift;

					//	// TimeMessage могут пропускаться при наличии других месседжей, поэтому событие
					//	// MarketTimeChanged необходимо вызывать при обработке времени из любых месседжей.
					//	break;

					case MessageTypes.SubscriptionResponse:
						ProcessSubscriptionResponseMessage((SubscriptionResponseMessage)message);
						break;

					case MessageTypes.SubscriptionFinished:
						ProcessSubscriptionFinishedMessage((SubscriptionFinishedMessage)message);
						break;

					case MessageTypes.SubscriptionOnline:
						ProcessSubscriptionOnlineMessage((SubscriptionOnlineMessage)message);
						break;

					case MessageTypes.Error:
						ProcessErrorMessage((ErrorMessage)message);
						break;

					case ExtendedMessageTypes.RemoveSecurity:
						ProcessSecurityRemoveMessage((SecurityRemoveMessage)message);
						break;

					case MessageTypes.ChangePassword:
						ProcessChangePasswordMessage((ChangePasswordMessage)message);
						break;

					default:
					{
						if (message is CandleMessage candleMsg)
							ProcessCandleMessage(candleMsg);
						else if (message is ISubscriptionIdMessage subscrMsg)
							ProcessSubscriptionMessage(subscrMsg);

						// если адаптеры передают специфичные сообщения
						// throw new ArgumentOutOfRangeException(LocalizedStrings.Str2142Params.Put(message.Type));
						break;
					}
				}
			}
			catch (Exception ex)
			{
				RaiseError(new InvalidOperationException(LocalizedStrings.Str681Params.Put(message), ex));
			}
		}

		private void ProcessSubscriptionResponseMessage(SubscriptionResponseMessage replyMsg)
		{
			var error = replyMsg.Error;

			var subscription = _subscriptionManager.ProcessResponse(replyMsg, out var originalMsg, out var unexpectedCancelled);

			if (originalMsg == null)
			{
				if (error != null)
					RaiseError(error);

				return;
			}

			if (originalMsg is MarketDataMessage mdMdg)
			{
				if (originalMsg.IsSubscribe)
				{
					if (replyMsg.IsOk())
						RaiseMarketDataSubscriptionSucceeded(mdMdg, subscription);
					else
					{
						if (unexpectedCancelled)
							RaiseMarketDataUnexpectedCancelled(mdMdg, error ?? new NotSupportedException(LocalizedStrings.SubscriptionNotSupported.Put(originalMsg)), subscription);
						else
							RaiseMarketDataSubscriptionFailed(mdMdg, replyMsg, subscription);
					}
				}
				else
				{
					if (replyMsg.IsOk())
						RaiseMarketDataUnSubscriptionSucceeded(mdMdg, subscription);
					else
						RaiseMarketDataUnSubscriptionFailed(mdMdg, replyMsg, subscription);
				}
			}
			else
			{
				if (error == null)
					RaiseSubscriptionStarted(subscription);
				else
				{
					RaiseSubscriptionFailed(subscription, error, originalMsg.IsSubscribe);

					if (originalMsg is OrderStatusMessage orderLookup)
						RaiseOrderStatusFailed(orderLookup.TransactionId, error, replyMsg.LocalTime);
					else if (originalMsg is SecurityLookupMessage secLookup)
						RaiseLookupSecuritiesResult(secLookup, error, Securities.Filter(secLookup).ToArray(), Array.Empty<Security>());
					else if (originalMsg is BoardLookupMessage boardLookup)
						RaiseLookupBoardsResult(boardLookup, error, ExchangeBoards.Filter(boardLookup).ToArray(), Array.Empty<ExchangeBoard>());
					else if (originalMsg is PortfolioLookupMessage pfLookup)
						RaiseLookupPortfoliosResult(pfLookup, error, Portfolios.Filter(pfLookup).ToArray(), Array.Empty<Portfolio>());
					else if (originalMsg is TimeFrameLookupMessage tfLookup)
						RaiseLookupTimeFramesResult(tfLookup, error, Array.Empty<TimeSpan>(), Array.Empty<TimeSpan>());
				}
			}
		}

		private void ProcessSubscriptionFinishedMessage(SubscriptionFinishedMessage message)
		{
			var subscription = _subscriptionManager.ProcessSubscriptionFinishedMessage(message, out var items);

			if (subscription == null)
				return;

			RaiseMarketDataSubscriptionFinished(message, subscription);

			ProcessSubscriptionResult(subscription, items);
		}

		private void ProcessSubscriptionOnlineMessage(SubscriptionOnlineMessage message)
		{
			var subscription = _subscriptionManager.ProcessSubscriptionOnlineMessage(message, out var items);

			if (subscription == null)
				return;

			RaiseMarketDataSubscriptionOnline(subscription);

			ProcessSubscriptionResult(subscription, items);
		}

		private void ProcessSubscriptionResult(Subscription subscription, object[] items)
		{
			T[] Typed<T>() => items.Cast<T>().ToArray();

			if (subscription.SubscriptionMessage is SecurityLookupMessage secLookup)
			{
				RaiseLookupSecuritiesResult(secLookup, null, Securities.Filter(secLookup).ToArray(), Typed<Security>());
			}
			else if (subscription.SubscriptionMessage is BoardLookupMessage boardLookup)
			{
				RaiseLookupBoardsResult(boardLookup, null, ExchangeBoards.Filter(boardLookup).ToArray(), Typed<ExchangeBoard>());
			}
			else if (subscription.SubscriptionMessage is PortfolioLookupMessage pfLookup)
			{
				RaiseLookupPortfoliosResult(pfLookup, null, Portfolios.Filter(pfLookup).ToArray(), Typed<Portfolio>());
			}
			else if (subscription.SubscriptionMessage is TimeFrameLookupMessage tfLookup)
			{
				RaiseLookupTimeFramesResult(tfLookup, null, Typed<TimeSpan>(), Typed<TimeSpan>());
			}
		}

		private void ProcessSecurityRemoveMessage(SecurityRemoveMessage message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var securityId = message.SecurityId;

			var security = SecurityStorage.LookupById(securityId);

			if (security != null)
			{
				SecurityStorage.Delete(security);
				_removed?.Invoke(new[] { security });
			}
		}

		private void ProcessConnectMessage(ConnectMessage message)
		{
			var adapter = message.Adapter;
			var error = message.Error;

			if (error == null)
			{
				if (adapter == Adapter)
				{
					_subscriptionManager.HandleConnected(_lookupMessagesOnConnect.Cache.Where(mt => Adapter.IsMessageSupported(mt)).ToArray());

					// raise event after re subscriptions cause handler on Connected event can send some subscriptions
					RaiseConnected();
				}
				else
					RaiseConnectedEx(adapter);
			}
			else
			{
				if (adapter == Adapter)
				{
					RaiseConnectionError(error);

					if (error is TimeoutException)
						RaiseTimeOut();
				}
				else
					RaiseConnectionErrorEx(adapter, error);
			}
		}

		private void ProcessDisconnectMessage(DisconnectMessage message)
		{
			var adapter = message.Adapter;
			var error = message.Error;

			if (error == null)
			{
				if (adapter == Adapter)
					RaiseDisconnected();
				else
					RaiseDisconnectedEx(adapter);
			}
			else
			{
				if (adapter == Adapter)
					RaiseConnectionError(error);
				else
					RaiseConnectionErrorEx(adapter, error);
			}
		}

		private void ProcessReconnectingStartedMessage(Message message)
		{
			RaiseConnectionLost(message.Adapter);
		}

		private void ProcessReconnectingFinishedMessage(Message message)
		{
			RaiseConnectionRestored(message.Adapter);
		}

		private void ProcessBoardStateMessage(BoardStateMessage message)
		{
			ExchangeBoard board;

			if (message.BoardCode.IsEmpty())
				board = null;
			else
			{
				board = ExchangeInfoProvider.GetOrCreateBoard(message.BoardCode);
				_entityCache.SetSessionState(board, message.State);
			}

			RaiseSessionStateChanged(board, message.State);
			RaiseReceived(board, message, BoardReceived);
		}

		private void ProcessBoardMessage(BoardMessage message)
		{
			var board = ExchangeInfoProvider.GetOrCreateBoard(message.Code, out var isNew, code =>
			{
				var exchange = message.ToExchange(EntityFactory.CreateExchange(message.ExchangeCode));
				var b = EntityFactory.CreateBoard(code, exchange);
				return b.ApplyChanges(message);
			});

			var subscriptions = _subscriptionManager.ProcessLookupResponse(message, board);
			RaiseReceived(board, subscriptions, BoardReceived);
		}

		private void ProcessSecurityMessage(SecurityMessage message)
		{
			var security = GetSecurity(message.SecurityId, s =>
			{
				if (!UpdateSecurityByDefinition)
					return false;

				s.ApplyChanges(message, ExchangeInfoProvider, OverrideSecurityData);
				return true;
			}, out var isNew);

			var subscriptions = _subscriptionManager.ProcessLookupResponse(message, security);
			RaiseReceived(security, subscriptions, SecurityReceived);
		}

		private void ProcessTimeFrameInfoMessage(TimeFrameInfoMessage message)
		{
			_subscriptionManager.ProcessLookupResponse(message, message.TimeFrames);

			// TODO
			//RaiseReceived(security, message, TimeFrameReceived);
		}

		private void ProcessLevel1ChangeMessage(Level1ChangeMessage message)
		{
			var security = EnsureGetSecurity(message);

			if (RaiseReceived(message, message, RaiseLevel1Received, out var anyCanOnline) == false)
			{
				if (anyCanOnline != true || _entityCache.HasLevel1Info(security))
					return;
			}

			if (UpdateSecurityByLevel1)
			{
				security.ApplyChanges(message);
				RaiseSecurityChanged(security);
			}

			var time = message.ServerTime;
			var info = _entityCache.GetSecurityValues(security, time);

			var changes = message.Changes;
			var cloned = false;

			foreach (var change in message.Changes)
			{
				var field = change.Key;

				if (!info.CanLastTrade && field.IsLastTradeField())
				{
					if (!cloned)
					{
						changes = changes.ToDictionary();
						cloned = true;
					}

					changes.Remove(field);

					continue;
				}

				if (!info.CanBestQuotes && (field.IsBestBidField() || field.IsBestAskField()))
				{
					if (!cloned)
					{
						changes = changes.ToDictionary();
						cloned = true;
					}

					changes.Remove(field);

					continue;
				}

				info.SetValue(time, field, change.Value);
			}

			if (changes.Count > 0)
				RaiseValuesChanged(security, message.Changes, message.ServerTime, message.LocalTime);
		}

		/// <inheritdoc />
		public Portfolio LookupByPortfolioName(string name) => GetPortfolio(name, null, out _);

		/// <summary>
		/// To get the portfolio by the code name.
		/// </summary>
		/// <param name="name">Portfolio code name.</param>
		/// <returns>The got portfolio. If there is no portfolio by given criteria, <see langword="null" /> is returned.</returns>
		public Portfolio GetPortfolio(string name) => LookupByPortfolioName(name);

		private Portfolio GetPortfolio(string name, Func<Portfolio, bool> changePortfolio, out bool isNew)
		{
			if (name.IsEmpty())
				throw new ArgumentNullException(nameof(name));

			var portfolio = PositionStorage.GetOrCreatePortfolio(name, key =>
			{
				var p = EntityFactory.CreatePortfolio(key);

				if (p == null)
					throw new InvalidOperationException(LocalizedStrings.Str1104Params.Put(name));

				//if (p.ExtensionInfo == null)
				//	p.ExtensionInfo = new Dictionary<string, object>();

				return p;
			}, out isNew);

			var isChanged = false;
			if (changePortfolio != null)
				isChanged = changePortfolio(portfolio);

			if (_existingPortfolios.TryAdd(portfolio))
			{
				this.AddInfoLog(LocalizedStrings.Str1105Params, portfolio.Name);
				RaiseNewPortfolio(portfolio);
			}
			else if (isChanged)
				RaisePortfolioChanged(portfolio);

			return portfolio;
		}

		private void TrySubscribePortfolio(Portfolio portfolio, IMessageAdapter adapter)
		{
			if (!IsAutoPortfoliosSubscribe || adapter?.IsSupportSubscriptionByPortfolio() != true)
				return;

			var subscription = _subscriptionManager.TryGetSubscription(portfolio);

			if (subscription == null)
				RegisterPortfolio(portfolio);
		}

		private void ProcessPortfolioMessage(PortfolioMessage message)
		{
			var portfolio = GetPortfolio(message.PortfolioName, p =>
			{
				message.ToPortfolio(p, ExchangeInfoProvider);
				return true;
			}, out var isNew);

			//if (message.OriginalTransactionId == 0)
			//	return;

			if (isNew)
				_subscriptionManager.ProcessLookupResponse(message, portfolio);

			RaiseReceived(portfolio, message, PortfolioReceived);
			TrySubscribePortfolio(portfolio, message.Adapter);
		}

		private void ProcessPositionChangeMessage(PositionChangeMessage message)
		{
			if (!KeepStrategiesPositions && !message.StrategyId.IsEmpty())
				return;

			Portfolio portfolio;

			if (message.IsMoney())
			{
				portfolio = GetPortfolio(message.PortfolioName, pf =>
				{
					if (message.LimitType != null)
						return false;

					pf.ApplyChanges(message, ExchangeInfoProvider);
					return true;
				}, out _);

				RaiseReceived(portfolio, message, PortfolioReceived);
			}

			var security = EnsureGetSecurity(message);
			portfolio = LookupByPortfolioName(message.PortfolioName);

			var valueInLots = message.TryGetDecimal(PositionChangeTypes.CurrentValueInLots);
			if (valueInLots != null)
			{
				if (!message.Changes.ContainsKey(PositionChangeTypes.CurrentValue))
				{
					var currValue = (decimal)valueInLots / (security.VolumeStep ?? 1);
					message.Add(PositionChangeTypes.CurrentValue, currValue);
				}

				message.Changes.Remove(PositionChangeTypes.CurrentValueInLots);
			}

			var position = GetPosition(portfolio, security, message.StrategyId, message.Side, message.ClientCode, message.DepoName, message.LimitType, message.Description);
			position.ApplyChanges(message);

			RaisePositionChanged(position);
			RaiseReceived(position, message, PositionReceived);

			TrySubscribePortfolio(portfolio, message.Adapter);
		}

		private void ProcessNewsMessage(NewsMessage message)
		{
			var security = message.SecurityId == null ? null : GetSecurity(message.SecurityId.Value);

			var news = _entityCache.ProcessNewsMessage(security, message);

			if (RaiseReceived(news.Item1, message, NewsReceived) == false)
				return;

			if (news.Item2)
				RaiseNewNews(news.Item1);
			else
				RaiseNewsChanged(news.Item1);
		}

		private void ProcessQuotesMessage(QuoteChangeMessage message)
		{
			var security = EnsureGetSecurity(message);

			var hasOnline = false;

			var receivedEvt = MarketDepthReceived;
			var hasReceivedEvt = receivedEvt != null;

			foreach (var subscription in _subscriptionManager.GetSubscriptions(message))
			{
				MarketDepth depth = null;

				if (!hasOnline)
				{
					if (subscription.State == SubscriptionStates.Online)
					{
						hasOnline = true;

						_entityCache.UpdateMarketDepth(security, message);

						if (MarketDepthChanged != null || MarketDepthsChanged != null || hasReceivedEvt || FilteredMarketDepthChanged != null)
						{
							depth = GetMarketDepth(security, message);

							message.ToMarketDepth(depth);

							if (message.IsFiltered)
								RaiseFilteredMarketDepthChanged(depth);
							else
								RaiseMarketDepthChanged(depth);
						}
					}
					else
					{
						if (subscription.State == SubscriptionStates.Active && subscription.SubscriptionMessage.To is null && !_entityCache.HasMarketDepth(security, message))
							_entityCache.UpdateMarketDepth(security, message);

						if (hasReceivedEvt)
							depth = message.ToMarketDepth(EntityFactory.CreateMarketDepth(security));
					}
				}
				else
				{
					if (hasReceivedEvt)
						depth = message.ToMarketDepth(EntityFactory.CreateMarketDepth(security));
				}

				OrderBookReceived?.Invoke(subscription, message);
				RaiseSubscriptionReceived(subscription, message);

				if (hasReceivedEvt)
					receivedEvt.Invoke(subscription, depth);
			}

			if (!hasOnline || message.IsFiltered || message.State != null)
				return;

			var bestBid = message.GetBestBid();
			var bestAsk = message.GetBestAsk();
			var fromLevel1 = message.BuildFrom == DataType.Level1;
			var time = message.ServerTime;

			if (!fromLevel1 && !Adapter.Level1Extend && (bestBid != null || bestAsk != null))
			{
				var info = _entityCache.GetSecurityValues(security, time);

				info.ClearBestQuotes(time);

				var changes = new List<KeyValuePair<Level1Fields, object>>(4);

				if (bestBid != null)
				{
					var q = bestBid.Value;

					info.SetValue(time, Level1Fields.BestBidPrice, q.Price);
					changes.Add(new (Level1Fields.BestBidPrice, q.Price));

					if (q.Volume != 0)
					{
						info.SetValue(time, Level1Fields.BestBidVolume, q.Volume);
						changes.Add(new (Level1Fields.BestBidVolume, q.Volume));
					}
				}

				if (bestAsk != null)
				{
					var q = bestAsk.Value;

					info.SetValue(time, Level1Fields.BestAskPrice, q.Price);
					changes.Add(new (Level1Fields.BestAskPrice, q.Price));

					if (q.Volume != 0)
					{
						info.SetValue(time, Level1Fields.BestAskVolume, q.Volume);
						changes.Add(new (Level1Fields.BestAskVolume, q.Volume));
					}
				}

				RaiseValuesChanged(security, changes, message.ServerTime, message.LocalTime);
			}

			if (UpdateSecurityLastQuotes)
			{
				var updated = false;

				if (!fromLevel1 || bestBid != null)
				{
					updated = true;
					security.BestBid = bestBid;
				}

				if (!fromLevel1 || bestAsk != null)
				{
					updated = true;
					security.BestAsk = bestAsk;
				}

				if (updated)
				{
					security.LocalTime = message.LocalTime;
					security.LastChangeTime = message.ServerTime;

					RaiseSecurityChanged(security);

					// стаканы по ALL обновляют BestXXX по конкретным инструментам
					if (security.Board?.Code == SecurityId.AssociatedBoardCode)
					{
						var changedSecurities = new Dictionary<Security, RefPair<bool, bool>>();

						foreach (var bid in message.Bids)
						{
							if (bid.BoardCode.IsEmpty())
								continue;

							var innerSecurity = GetSecurity(new SecurityId
							{
								SecurityCode = security.Code,
								BoardCode = bid.BoardCode
							});

							var info = changedSecurities.SafeAdd(innerSecurity);

							if (info.First)
								continue;

							info.First = true;

							innerSecurity.BestBid = bid;
							innerSecurity.LocalTime = message.LocalTime;
							innerSecurity.LastChangeTime = message.ServerTime;
						}

						foreach (var ask in message.Asks)
						{
							if (ask.BoardCode.IsEmpty())
								continue;

							var innerSecurity = GetSecurity(new SecurityId
							{
								SecurityCode = security.Code,
								BoardCode = ask.BoardCode
							});

							var info = changedSecurities.SafeAdd(innerSecurity);

							if (info.Second)
								continue;

							info.Second = true;

							innerSecurity.BestAsk = ask;
							innerSecurity.LocalTime = message.LocalTime;
							innerSecurity.LastChangeTime = message.ServerTime;
						}

						RaiseSecuritiesChanged(changedSecurities.Keys.ToArray());
					}
				}
			}
		}

		private void ProcessOrderLogMessage(ExecutionMessage message)
		{
			var security = EnsureGetSecurity(message);

			var trade = (message.TradeId != null || !message.TradeStringId.IsEmpty())
				? EntityFactory.CreateTrade(security, message.TradeId, message.TradeStringId ?? string.Empty)
				: null;

			var logItem = message.ToOrderLog(EntityFactory.CreateOrderLogItem(new Order { Security = security }, trade));
			//logItem.LocalTime = message.LocalTime;

			if (RaiseReceived(logItem, message, OrderLogItemReceived) == false)
				return;

			RaiseNewOrderLogItem(logItem);
		}

		private void ProcessTradeMessage(ExecutionMessage message)
		{
			var security = EnsureGetSecurity(message);

			var tuple = _entityCache.ProcessTradeMessage(security, message);

			if (RaiseReceived(tuple.Item1, message, TickTradeReceived) == false)
				return;

			var time = message.ServerTime;
			var info = _entityCache.GetSecurityValues(security, time);

			info.ClearLastTrade(time);

			var price = message.TradePrice ?? 0;

			var changes = new List<KeyValuePair<Level1Fields, object>>(4)
			{
				new (Level1Fields.LastTradeTime, message.ServerTime),
				new (Level1Fields.LastTradePrice, price)
			};

			info.SetValue(time, Level1Fields.LastTradeTime, message.ServerTime);
			info.SetValue(time, Level1Fields.LastTradePrice, price);

			if (message.IsSystem != null)
			{
				info.SetValue(time, Level1Fields.IsSystem, message.IsSystem.Value);
				changes.Add(new (Level1Fields.IsSystem, message.IsSystem.Value));
			}

			if (message.TradeId != null)
			{
				info.SetValue(time, Level1Fields.LastTradeId, message.TradeId.Value);
				changes.Add(new (Level1Fields.LastTradeId, message.TradeId.Value));
			}

			if (!message.TradeStringId.IsEmpty())
			{
				info.SetValue(time, Level1Fields.LastTradeStringId, message.TradeStringId);
				changes.Add(new (Level1Fields.LastTradeStringId, message.TradeStringId));
			}

			if (message.TradeVolume != null)
			{
				info.SetValue(time, Level1Fields.LastTradeVolume, message.TradeVolume.Value);
				changes.Add(new (Level1Fields.LastTradeVolume, message.TradeVolume.Value));
			}

			if (message.OriginSide != null)
			{
				info.SetValue(time, Level1Fields.LastTradeOrigin, message.OriginSide.Value);
				changes.Add(new (Level1Fields.LastTradeOrigin, message.OriginSide.Value));
			}

			if (message.IsUpTick != null)
			{
				info.SetValue(time, Level1Fields.LastTradeUpDown, message.IsUpTick.Value);
				changes.Add(new (Level1Fields.LastTradeUpDown, message.IsUpTick.Value));
			}

			if (tuple.Item2)
				RaiseNewTrade(tuple.Item1);

			RaiseValuesChanged(security, changes, message.ServerTime, message.LocalTime);

			if (!UpdateSecurityLastQuotes)
				return;

			security.LastTrade = tuple.Item1;
			security.LastChangeTime = tuple.Item1.Time;

			RaiseSecurityChanged(security);
		}

		private void ProcessOrderMessage(Order o, Security security, ExecutionMessage message, long transactionId/*, bool isStatusRequest*/)
		{
			if (message.OrderState != OrderStates.Failed && message.Error == null)
			{
				foreach (var change in _entityCache.ProcessOrderMessage(o, security, message, transactionId, LookupByPortfolioName))
				{
					if (change == EntityCache.OrderChangeInfo.NotExist)
					{
						this.AddWarningLog(LocalizedStrings.Str1156Params, message.OrderId.To<string>() ?? message.OrderStringId);
						continue;
					}

					var order = change.Order;

					_entityCache.TrySetAdapter(order, message.Adapter);

					if (change.IsNew)
					{
						this.AddOrderInfoLog(order, "New order");

						RaiseNewOrder(order);
					}
					else if (change.IsChanged)
					{
						this.AddOrderInfoLog(order, "Order changed");

						RaiseOrderChanged(order);

						if (change.IsEdit)
							RaiseOrderEdited(transactionId, order);
					}

					RaiseReceived(order, message, OrderReceived);
				}
			}
			else
			{
				if (message.OriginalTransactionId == 0)
				{
					this.AddErrorLog("Unknown error response for order {0}: {1}.", o, message.Error);
					return;
				}

				foreach (var tuple in _entityCache.ProcessOrderFailMessage(o, security, message))
				{
					var fail = tuple.Item1;
					var order = fail.Order;

					_entityCache.TrySetAdapter(order, message.Adapter);

					//TryProcessFilteredMarketDepth(fail.Order.Security, message);

					//var isRegisterFail = (fail.Order.Id == null && fail.Order.StringId.IsEmpty()) || fail.Order.Status == OrderStatus.RejectedBySystem;
					var operation = tuple.Item2;

					_entityCache.AddFail(operation, fail);

					switch (operation)
					{
						case OrderOperations.Register:
						{
							RaiseOrderRegisterFailed(message.OriginalTransactionId, fail);
							RaiseReceived(fail, message, OrderRegisterFailReceived);
							break;
						}
						case OrderOperations.Cancel:
						{
							RaiseOrderCancelFailed(message.OriginalTransactionId, fail);
							RaiseReceived(fail, message, OrderCancelFailReceived);
							break;
						}
						case OrderOperations.Edit:
						{
							RaiseOrderEditFailed(message.OriginalTransactionId, fail);
							RaiseReceived(fail, message, OrderEditFailReceived);
							break;
						}
						default:
							throw new ArgumentOutOfRangeException(operation.ToString());
					}
				}
			}
		}

		private void ProcessOwnTradeMessage(Order order, Security security, ExecutionMessage message, long transactionId)
		{
			var tuple = _entityCache.ProcessOwnTradeMessage(order, security, message, transactionId);

			if (tuple == null)
				return;

			if (tuple.Item2)
				RaiseNewMyTrade(tuple.Item1);

			//this.AddWarningLog("Duplicate own trade message: {0}", message);
			RaiseReceived(tuple.Item1, message, OwnTradeReceived);
		}

		private void ProcessTransactionMessage(ExecutionMessage message)
		{
			var originId = message.OriginalTransactionId;

			if (_entityCache.IsMassCancelation(originId))
			{
				if (message.Error == null)
					RaiseMassOrderCanceled(originId, message.ServerTime);
				else
					RaiseMassOrderCancelFailed(originId, message.Error, message.ServerTime);

				return;
			}

			var isStatusRequest = _entityCache.IsOrderStatusRequest(originId);

			if (message.Error != null && isStatusRequest)
			{
				// TransId != 0 means contains failed order info (not just status response)
				if (message.TransactionId == 0)
				{
					RaiseOrderStatusFailed(originId, message.Error, message.ServerTime);
					return;
				}
			}

			Order order = null;

			var transactionId = message.TransactionId;

			if (transactionId == 0)
			{
				transactionId = isStatusRequest || _entityCache.IsMassCancelation(originId) ? 0 : originId;

				if (transactionId == 0)
					order = _entityCache.TryGetOrder(message.OrderId, message.OrderStringId);
			}

			if (transactionId != 0)
			{
				if (message.HasTradeInfo())
					order = _entityCache.TryGetOrder(transactionId, OrderOperations.Register);
				else
					order = _entityCache.TryGetOrder(transactionId, OrderOperations.Edit) ?? _entityCache.TryGetOrder(transactionId, OrderOperations.Cancel) ?? _entityCache.TryGetOrder(transactionId, OrderOperations.Register);
			}

			Security security;

			if (order == null)
			{
				if (message.SecurityId == default)
				{
					this.AddWarningLog(LocalizedStrings.Str1025);
					this.AddWarningLog(message.ToString());
					return;
				}

				security = EnsureGetSecurity(message);

				if (transactionId == 0 && isStatusRequest)
					transactionId = TransactionIdGenerator.GetNextId();
			}
			else
				security = order.Security;

			this.AddDebugLog("Order '{0}': {1}", order?.TransactionId, message);

			var processed = false;

			if (message.HasOrderInfo())
			{
				processed = true;
				ProcessOrderMessage(order, security, message, transactionId);
			}

			if (message.HasTradeInfo())
			{
				processed = true;
				ProcessOwnTradeMessage(order, security, message, transactionId);
			}

			if (!processed)
				throw new ArgumentOutOfRangeException(nameof(message), message.DataType, LocalizedStrings.Str1695Params.Put(message));
		}

		private void ProcessExecutionMessage(ExecutionMessage message)
		{
			if (message.DataType == DataType.Transactions)
				ProcessTransactionMessage(message);
			else if (message.DataType == DataType.Ticks)
				ProcessTradeMessage(message);
			else if (message.DataType == DataType.OrderLog)
				ProcessOrderLogMessage(message);
			else
				throw new ArgumentOutOfRangeException(nameof(message), message.DataType, LocalizedStrings.Str1695Params.Put(message));
		}

		private void ProcessCandleMessage(CandleMessage message)
		{
			foreach (var tuple in _subscriptionManager.UpdateCandles(message))
			{
				var subscription = tuple.Item1;
				var candle = tuple.Item2;

				if (subscription.CandleSeries != null)
					RaiseCandleSeriesProcessing(subscription.CandleSeries, candle);

				CandleReceived?.Invoke(subscription, candle);
				RaiseSubscriptionReceived(subscription, message);
			}
		}

		private void ProcessChangePasswordMessage(ChangePasswordMessage message)
		{
			RaiseChangePassword(message.OriginalTransactionId, message.Error);
		}

		private void ProcessSubscriptionMessage(ISubscriptionIdMessage subscrMsg)
		{
			RaiseReceived((Message)subscrMsg, subscrMsg, RaiseSubscriptionReceived);
		}

		private void ProcessErrorMessage(ErrorMessage message)
		{
			var error = message.Error;

			RaiseError(error);

			if (message.Adapter is not null and not BasketMessageAdapter)
				message.Adapter.AddErrorLog(error);
			else
				this.AddErrorLog(error);
		}
	}
}