#region Related components
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{

		#region Location
		/// <summary>
		/// Gets the current location (IP-based)
		/// </summary>
		public static string CurrentLocation { get; private set; } = "Unknown";

		/// <summary>
		/// Gets the location of the session (IP-based)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="ipAddress"></param>
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<string> GetLocationAsync(this Session session, string ipAddress, string correlationID, CancellationToken cancellationToken)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			var location = "Unknown";
			try
			{
				var service = Router.GetService("IPLocations");
				var requestInfo = new RequestInfo(session, "IPLocations")
				{
					Header = new Dictionary<string, string> { ["x-requester"] = "vieapps-ngx-base" },
					CorrelationID = correlationID
				};
				if (!string.IsNullOrWhiteSpace(ipAddress))
					requestInfo.Query["IP"] = ipAddress;
				var response = await service.ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
				var city = response.Get("City", "N/A");
				var region = response.Get("Region", "N/A");
				if (region.Equals(city) && !"N/A".IsEquals(city))
					region = "";
				var country = response.Get("Country", "N/A");

				if ("N/A".IsEquals(city) && "N/A".IsEquals(region) && "N/A".IsEquals(country) && "Unknown".IsEquals(Extensions.CurrentLocation))
				{
					response = await service.ProcessRequestAsync(new RequestInfo(requestInfo) { ObjectName = "Current" }, cancellationToken).ConfigureAwait(false);
					city = response.Get("City", "N/A");
					region = response.Get("Region", "N/A");
					if (region.Equals(city) && !"N/A".IsEquals(city))
						region = "";
					country = response.Get("Country", "N/A");
					location = Extensions.CurrentLocation = $"{city}, {region}, {country}".Replace(", ,", ",");
				}
				else
					location = $"{city}, {region}, {country}".Replace(", ,", ",");
			}
			catch {}
			return location;
		}

		/// <summary>
		/// Gets the location of the session (IP-based)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<string> GetLocationAsync(this Session session, string correlationID = null, CancellationToken cancellationToken = default)
			=> session == null
				? Task.FromResult("Unknown")
				: session.GetLocationAsync(null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the location of the request (IP-based)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<string> GetLocationAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> requestInfo.Session == null
				? Task.FromResult("Unknown")
				: requestInfo.Session.GetLocationAsync(null, requestInfo.CorrelationID, cancellationToken);
		#endregion

		#region Get encryption keys
		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, byte[] seeds = null)
			=> session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256);

		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, string seeds)
			=> session.GetEncryptionKey(seeds?.ToBytes());

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, byte[] seeds = null)
			=> session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, string seeds)
			=> session.GetEncryptionIV(seeds?.ToBytes());

		/// <summary>
		/// Encrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetEncryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Encrypt(session.GetEncryptionKey(keySeeds?.ToBytes()), session.GetEncryptionIV(ivSeeds?.ToBytes())).ToHex()
				: null;

		/// <summary>
		/// Decrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetDecryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Decrypt(session.GetEncryptionKey(keySeeds?.ToBytes()), session.GetEncryptionIV(ivSeeds?.ToBytes())).ToHex()
				: null;
		#endregion

		#region Check is system administrator/account
		/// <summary>
		/// Calls the Users service to check this user is system administrator or not
		/// </summary>
		/// <param name="user"></param>
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<bool> IsSystemAdministratorAsync(this IUser user, string correlationID = null, CancellationToken cancellationToken = default)
		{
			if (user == null || !user.IsAuthenticated)
				return false;

			var isSystemAdministrator = user.IsSystemAdministrator;
			if (!isSystemAdministrator)
			{
				var requestInfo = new RequestInfo(new Session { User = new User(user) }, "Users", "Account", "GET")
				{
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["IsSystemAdministrator"] = "" },
					CorrelationID = correlationID
				};
				var response = await requestInfo.CallServiceAsync(cancellationToken).ConfigureAwait(false);
				isSystemAdministrator = user.ID.IsEquals(response.Get<string>("ID")) && response.Get<bool>("IsSystemAdministrator");
			}
			return isSystemAdministrator;
		}

		/// <summary>
		/// Calls the Users service to check this user is system administrator or not
		/// </summary>
		/// <param name="session"></param>
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this Session session, string correlationID = null, CancellationToken cancellationToken = default)
			=> session == null || session.User == null
				? Task.FromResult(false)
				: session.User.IsSystemAdministratorAsync(correlationID, cancellationToken);

		/// <summary>
		/// Calls the Users service to check this user is system administrator or not
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> requestInfo == null || requestInfo.Session == null || requestInfo.Session.User == null
				? Task.FromResult(false)
				: requestInfo.Session.User.IsSystemAdministratorAsync(requestInfo.CorrelationID, cancellationToken);
		#endregion

		#region Send session state
		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="session"></param>
		/// <param name="serviceName"></param>
		/// <param name="serviceURI"></param>
		/// <param name="serviceSystemID"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="onUpdateMessagePrepared"></param>
		/// <param name="correlationID"></param>
		/// <returns></returns>
		public static async Task<Session> SendSessionStateAsync(this Session session, string serviceName, string serviceURI, string serviceSystemID, bool online, bool trackStatistics, bool sendClientMessage, Action<CommunicateMessage> onCommunicateMessagePrepared = null, Action<UpdateMessage> onUpdateMessagePrepared = null, string correlationID = null)
		{
			var communicateMessage = new CommunicateMessage("Users")
			{
				Type = "Session#State",
				Data = session.ToJson(json =>
				{
					json["SessionID"] = session.SessionID;
					json["UserID"] = session.User?.ID;
					json["Online"] = online;
					json["Track"] = online && trackStatistics;
					json["AppInfo"] = $"{session.AppName} @ {session.AppPlatform}";
					json["OSInfo"] = $"{Extensions.GetOSInfo(session.AppAgent)} [{session.AppAgent}]";			
				})
			};
			if (!string.IsNullOrWhiteSpace(serviceName) && !string.IsNullOrWhiteSpace(serviceURI))
				communicateMessage.Data["Service"] = new JObject
				{
					["Name"] = serviceName.ToLower(),
					["URI"] = serviceURI,
					["SystemID"] = string.IsNullOrWhiteSpace(serviceSystemID) ? null : serviceSystemID
				};
			if (!string.IsNullOrWhiteSpace(correlationID))
				communicateMessage.Data["CorrelationID"] = correlationID;
			onCommunicateMessagePrepared?.Invoke(communicateMessage);
			communicateMessage.Send(Router.GotBackupRouter());

			if (sendClientMessage && !string.IsNullOrWhiteSpace(session.User.ID))
			{
				var updateMessage = new UpdateMessage
				{
					Type = "Users#Session#State",
					DeviceID = "*",
					Data = new JObject
					{
						["SessionID"] = session.GetEncryptedID(session.SessionID),
						["UserID"] = session.User.ID,
						["DeviceID"] = session.DeviceID,
						["IsOnline"] = online,
						["Location"] = await session.GetLocationAsync().ConfigureAwait(false),
						["AppName"] = session.AppName,
						["AppPlatform"] = session.AppPlatform
					}
				};
				onUpdateMessagePrepared?.Invoke(updateMessage);
				updateMessage.Send();
			}
			return session;
		}

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="session"></param>
		/// <param name="serviceName"></param>
		/// <param name="serviceURI"></param>
		/// <param name="serviceSystemID"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="onUpdateMessagePrepared"></param>
		/// <param name="correlationID"></param>
		public static void SendSessionState(this Session session, string serviceName, string serviceURI, string serviceSystemID, bool online, bool trackStatistics, bool sendClientMessage, Action<CommunicateMessage> onCommunicateMessagePrepared = null, Action<UpdateMessage> onUpdateMessagePrepared = null, string correlationID = null)
			=> session.SendSessionStateAsync(serviceName, serviceURI, serviceSystemID, online, trackStatistics, sendClientMessage, onCommunicateMessagePrepared, onUpdateMessagePrepared, correlationID).Execute();

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="session"></param>
		/// <param name="serviceName"></param>
		/// <param name="serviceURI"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="onUpdateMessagePrepared"></param>
		/// <param name="correlationID"></param>
		public static void SendSessionState(this Session session, string serviceName, string serviceURI, bool online = true, bool trackStatistics = true, bool sendClientMessage = false, Action<CommunicateMessage> onCommunicateMessagePrepared = null, Action<UpdateMessage> onUpdateMessagePrepared = null, string correlationID = null)
			=> session.SendSessionState(serviceName, serviceURI, null, online, trackStatistics, sendClientMessage, onCommunicateMessagePrepared, onUpdateMessagePrepared, correlationID);

		/// <summary>
		/// Sends tracking statistics
		/// </summary>
		/// <param name="session"></param>
		public static void TrackStatistics(this Session session, string correlationID)
			=> new CommunicateMessage("Users")
			{
				Type = "Statistics#Track",
				Data = new JObject
				{
					["SessionID"] = session?.SessionID,
					["UserID"] = session?.User?.ID,
					["CorrelationID"] = correlationID
				}
			}.Send(Router.GotBackupRouter());
		#endregion

	}
}