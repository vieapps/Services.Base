#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		/// <summary>
		/// Gets the request body in JSON
		/// </summary>
		/// <returns></returns>
		public static JToken GetBodyJson(this RequestInfo requestInfo)
			=> requestInfo?.BodyAsJson;

		/// <summary>
		/// Gets the request body in dynamic object (ExpandoObject)
		/// </summary>
		/// <returns></returns>
		public static ExpandoObject GetBodyExpando(this RequestInfo requestInfo)
			=> requestInfo?.BodyAsExpandoObject;

		/// <summary>
		/// Gets the parameter from the header
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetHeaderParameter(this RequestInfo requestInfo, string name)
			=> requestInfo != null && requestInfo.Header != null && !string.IsNullOrWhiteSpace(name)
				? requestInfo.Header.TryGetValue(name, out var value) ? value : null
				: null;

		/// <summary>
		/// Gets the parameter from the query
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetQueryParameter(this RequestInfo requestInfo, string name)
			=> requestInfo != null && requestInfo.Query != null && !string.IsNullOrWhiteSpace(name)
				? requestInfo.Query.TryGetValue(name, out var value) ? value : null
				: null;

		/// <summary>
		/// Gets the parameter with two steps: first from header, then second step is from query if header has no value
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetParameter(this RequestInfo requestInfo, string name)
			=> requestInfo?.GetHeaderParameter(name) ?? requestInfo?.GetQueryParameter(name);

		/// <summary>
		/// Gets the identity of the device that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetDeviceID(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.DeviceID)
				? requestInfo.Session.DeviceID
				: requestInfo?.GetParameter("x-device-id");

		/// <summary>
		/// Gets the identity of the developer that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetDeveloperID(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.DeveloperID)
				? requestInfo.Session.DeveloperID
				: requestInfo?.GetParameter("x-developer-id");

		/// <summary>
		/// Gets the identity of the app that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetAppID(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.AppID)
				? requestInfo.Session.AppID
				: requestInfo?.GetParameter("x-app-id");

		/// <summary>
		/// Gets the name of the app that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetAppName(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.AppName)
				? requestInfo.Session.AppName
				: requestInfo?.GetParameter("x-app-name");

		/// <summary>
		/// Gets the platform of the app that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetAppPlatform(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.AppPlatform)
				? requestInfo.Session.AppPlatform
				: requestInfo?.GetParameter("x-app-platform");

		/// <summary>
		/// Gets the agent string of the app that sent by this request
		/// </summary>
		/// <returns></returns>
		public static string GetAppAgent(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && !string.IsNullOrWhiteSpace(requestInfo.Session.AppAgent)
				? requestInfo.Session.AppAgent
				: requestInfo?.GetParameter("user-agent");

		/// <summary>
		/// Gets the object identity (from the parameter named 'object-identity' of the query)
		/// </summary>
		/// <param name="requiredAsUUID">true to require object identity is valid UUID</param>
		/// <param name="getAlternative">true to get alternative identity via 'id', 'object-id', or 'x-object-id'</param>
		/// <returns></returns>
		public static string GetObjectIdentity(this RequestInfo requestInfo, bool requiredAsUUID = false, bool getAlternative = false)
		{
			var objectIdentity = requestInfo?.GetQueryParameter("object-identity");
			return !string.IsNullOrWhiteSpace(objectIdentity)
				? !requiredAsUUID
					? objectIdentity
					: objectIdentity.IsValidUUID()
						? objectIdentity
						: getAlternative
							? requestInfo.GetQueryParameter("id") ?? requestInfo.GetQueryParameter("object-id") ?? requestInfo.GetQueryParameter("x-object-id")
							: null
				: getAlternative
					? requestInfo.GetQueryParameter("id") ?? requestInfo.GetQueryParameter("object-id") ?? requestInfo.GetQueryParameter("x-object-id")
					: null;
		}

		/// <summary>
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to JSON
		/// </summary>
		/// <returns></returns>
		public static JToken GetRequestJson(this RequestInfo requestInfo)
			=> (requestInfo?.GetQueryParameter("x-request")?.Url64Decode() ?? "{}").ToJson();

		/// <summary>
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to ExpandoObject
		/// </summary>
		/// <returns></returns>
		public static ExpandoObject GetRequestExpando(this RequestInfo requestInfo)
			=> requestInfo?.GetRequestJson()?.ToExpandoObject() ?? new ExpandoObject();

		/// <summary>
		/// Gets the full URI
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="transformer"></param>
		/// <returns></returns>
		public static string GetURI(this RequestInfo requestInfo, Func<string, string> transformer = null)
		{
			var uri = $"/{requestInfo?.ServiceName ?? ""}".ToLower();
			if (!string.IsNullOrWhiteSpace(requestInfo?.ObjectName))
			{
				uri += $"/{requestInfo.ObjectName.ToLower()}";
				var objectIdentity = requestInfo.GetObjectIdentity();
				if (!string.IsNullOrWhiteSpace(objectIdentity))
				{
					uri += $"/{objectIdentity.ToLower()}";
					if (!objectIdentity.IsValidUUID())
					{
						if (requestInfo.Query != null && requestInfo.Query.TryGetValue("x-request", out var request))
							uri += $"?x-request={request}";
						else
						{
							var objectID = requestInfo.GetObjectIdentity(true, true);
							if (!string.IsNullOrWhiteSpace(objectID))
								uri += $"/{objectID}";
						}
					}
				}
			}
			return transformer != null ? transformer(uri) : uri;
		}

		/// <summary>
		/// Gets profile of collection of users
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userIDs"></param>
		/// <param name="fetchSessions"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> GetUserProfilesAsync(this RequestInfo requestInfo, IEnumerable<string> userIDs, bool fetchSessions = true, CancellationToken cancellationToken = default)
			=> new RequestInfo(requestInfo.Session, "Users", "Profile", "GET")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", "fetch" },
					{ "x-request", new JObject { { "IDs", userIDs.ToJArray() } }.ToString(Newtonsoft.Json.Formatting.None).Url64Encode() }
				},
				Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "x-notifications-key", UtilityService.GetAppSetting("Keys:Notifications", "") },
					{ "x-fetch-sessions", fetchSessions.ToString().ToLower() }
				},
				CorrelationID = requestInfo.CorrelationID
			}.CallServiceAsync(cancellationToken);

		/// <summary>
		/// Gets the sessions of an user. 1st element is session identity, 2nd element is device identity, 3rd element is app info, 4th element is online status
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<List<Tuple<string, string, string, bool>>> GetUserSessionsAsync(this RequestInfo requestInfo, string userID = null, CancellationToken cancellationToken = default)
		{
			var result = await new RequestInfo(requestInfo.Session, "Users", "Account", "HEAD")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", userID ?? requestInfo.Session.User.ID }
				},
				CorrelationID = requestInfo.CorrelationID
			}.CallServiceAsync(cancellationToken).ConfigureAwait(false);
			return (result["Sessions"] as JArray).ToList(info => new Tuple<string, string, string, bool>(info.Get<string>("SessionID"), info.Get<string>("DeviceID"), info.Get<string>("AppInfo"), info.Get<bool>("IsOnline")));
		}

		/// <summary>
		/// Sends an app notification (using Notifications service)
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="senderID">The identity of an user who send this notification</param>
		/// <param name="senderName">The name of an user who send this notification</param>
		/// <param name="recipients">The collection of user identities</param>
		/// <param name="detail">The detail of this notification</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public static async Task SendNotificationAsync(this RequestInfo requestInfo, string senderID, string senderName, IEnumerable<string> recipients, JObject detail, CancellationToken cancellationToken = default)
		{
			// prepare sender information
			senderID = senderID ?? requestInfo.Session.User.ID;
			if (string.IsNullOrWhiteSpace(senderName))
			{
				var sender = (await requestInfo.GetUserProfilesAsync(new[] { senderID }, false, cancellationToken).ConfigureAwait(false) as JArray)?.FirstOrDefault();
				senderName = sender?.Get<string>("Name") ?? "Unknown";
			}

			// prepare body
			detail = detail ?? new JObject();
			var body = new JObject
			{
				{ "Time", DateTime.Now },
				{ "Action", detail.Get<string>("Action") ?? detail.Get("Event", "Update") },
				{ "SenderID", senderID },
				{ "SenderName", senderName },
				{ "Recipients", recipients?.ToJArray() },
				{ "ServiceName", requestInfo.ServiceName },
				{ "ObjectName", requestInfo.ObjectName },
				{ "Title", detail.Get<string>("Title") ?? detail.Get<string>("ObjectTitle") },
			};
			var excluded = new[] { "Time", "Sender", "SenderID", "SenderName", "Recipients", "RecipientIDs", "RecipientID", "Action", "Event", "ServiceName", "ServiceName", "ObjectName", "SystemID", "RepositoryID", "RepositoryEntityID", "ObjectID", "Title", "ObjectTitle", "Status", "PreviousStatus", "Additionals" }.ToHashSet();
			var excludedOfBody = new[] { "Time", "Sender", "SenderID", "SenderName", "Recipients", "RecipientIDs", "RecipientID", "Action", "Event", "ServiceName", "ServiceName", "ObjectName", "Title", "ObjectTitle", "Additionals" }.ToHashSet();
			var additionals = new JObject();
			detail.ForEach(kvp =>
			{
				if (!excluded.Contains(kvp.Key))
					additionals[kvp.Key] = kvp.Value;
				else if (!excludedOfBody.Contains(kvp.Key))
					body[kvp.Key] = kvp.Value;
			});
			detail["Additionals"] = additionals;

			// send the notification
			await new RequestInfo(requestInfo.Session, "Notifications", "Notification", "POST")
			{
				Body = body.ToString(Newtonsoft.Json.Formatting.None),
				Extra = new Dictionary<string, string>(requestInfo.Extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
				{
					{ "x-notifications-key", UtilityService.GetAppSetting("Keys:Notifications", "") }
				},
				CorrelationID = requestInfo.CorrelationID
			}.CallServiceAsync(cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Converts and validates this request information as a web-hook message
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="secretToken"></param>
		/// <param name="secretTokenName"></param>
		/// <param name="signAlgorithm"></param>
		/// <param name="signKey"></param>
		/// <param name="signKeyIsHex"></param>
		/// <param name="signatureName"></param>
		/// <param name="signatureAsHex"></param>
		/// <param name="requiredQuery"></param>
		/// <param name="requiredHeader"></param>
		/// <param name="decryptionKey"></param>
		/// <param name="decryptionIV"></param>
		/// <returns></returns>
		public static WebHookMessage ToWebHookMessage(this RequestInfo requestInfo, string secretToken, string secretTokenName, string signAlgorithm, string signKey, bool signKeyIsHex, string signatureName, bool signatureAsHex, IDictionary<string, string> requiredQuery, IDictionary<string, string> requiredHeader, byte[] decryptionKey, byte[] decryptionIV)
			=> new WebHookMessage
			{
				EndpointURL = requestInfo.Session?.AppOrigin ?? requestInfo.GetHeaderParameter("Origin"),
				Body = requestInfo.Body,
				Query = requestInfo.Query,
				Header = requestInfo.Header,
				CorrelationID = requestInfo.CorrelationID
			}.Validate(secretToken, secretTokenName, signAlgorithm, signKey, signKeyIsHex, signatureName, signatureAsHex, requiredQuery, requiredHeader, decryptionKey, decryptionIV);

		/// <summary>
		/// Converts and validates this request information as a web-hook message
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="secretToken"></param>
		/// <param name="secretTokenName"></param>
		/// <param name="signAlgorithm"></param>
		/// <param name="signKey"></param>
		/// <param name="signKeyIsHex"></param>
		/// <param name="signatureName"></param>
		/// <param name="signatureAsHex"></param>
		/// <param name="requiredQuery"></param>
		/// <param name="requiredHeader"></param>
		/// <param name="decryptionKey"></param>
		/// <param name="decryptionIV"></param>
		/// <returns></returns>
		public static WebHookMessage ToWebHookMessage(this RequestInfo requestInfo, string secretToken, string secretTokenName, string signAlgorithm, string signKey, bool signKeyIsHex, string signatureName, bool signatureAsHex, JObject requiredQuery = null, JObject requiredHeader = null, byte[] decryptionKey = null, byte[] decryptionIV = null)
			=> requestInfo?.ToWebHookMessage(secretToken, secretTokenName, signAlgorithm, signKey, signKeyIsHex, signatureName, signatureAsHex, requiredQuery?.ToDictionary<string>(), requiredHeader?.ToDictionary<string>(), decryptionKey, decryptionIV);

		static string GetValue(this Dictionary<string, string> dictionary, string name)
			=> dictionary.TryGetValue(name, out var @string) ? @string : null;

		static Dictionary<string, string> GetDictionary(this Dictionary<string, string> dictionary, string name)
			=> (dictionary.GetValue(name)?.ToJson() as JObject)?.ToDictionary<string>();

		/// <summary>
		/// Forwards a request as a web-hook message
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="settings"></param>
		/// <param name="paramsJson"></param>
		/// <param name="secretToken"></param>
		/// <param name="secretTokenName"></param>
		/// <param name="writeLogsAsync"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<JToken> ForwardAsWebHookMessageAsync(this RequestInfo requestInfo, WebHookInfo settings, JToken paramsJson = null, string secretToken = null, string secretTokenName = "x-webhook-secret-token", Func<Exception, string, Task> writeLogsAsync = null, CancellationToken cancellationToken = default)
		{
			var signKey = settings.SignKey ?? requestInfo.GetAppID() ?? requestInfo.GetDeveloperID();
			var webhookQuery = settings.QueryAsJson;
			var webhookHeader = settings.HeaderAsJson;
			var encryptionKey = settings.EncryptionKey?.HexToBytes();
			var encryptionIV = settings.EncryptionIV?.HexToBytes();

			var message = requestInfo.ToWebHookMessage(secretToken, secretTokenName, settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, webhookQuery, webhookHeader, encryptionKey, encryptionIV);
			var messageJson = new JObject
			{
				["Header"] = message.Header.ToJObject(),
				["Query"] = message.Query.ToJObject(),
				["Body"] = requestInfo.BodyAsJson
			};

			var verb = "POST";
			var debugLogs = "";
			var writeLogs = requestInfo.Query.TryGetValue("x-logs", out var _);
			var jsonFormat = writeLogs ? Formatting.Indented : Formatting.None;

			if (message.Header.TryGetValue("x-webhook-pre-endpoint-url", out var preEndpointURL))
			{
				verb = message.Header.TryGetValue("x-webhook-pre-verb", out var preVerb) ? preVerb : "POST";
				var status = "OK";
				JToken response = null;

				message = new WebHookMessage
				{
					EndpointURL = preEndpointURL,
					Header = message.Header.GetDictionary("x-webhook-pre-header"),
					Query = message.Header.GetDictionary("x-webhook-pre-query"),
					Body = message.Header.GetValue("x-webhook-pre-body") ?? "{}",
				}.Normalize(settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, webhookQuery?.ToDictionary<string>(), webhookHeader?.ToDictionary<string>(), encryptionKey, encryptionIV);
				if (!string.IsNullOrWhiteSpace(secretToken))
					message.Header[string.IsNullOrWhiteSpace(secretTokenName) ? "x-webhook-secret-token" : secretTokenName] = secretToken;

				try
				{
					using (var httpResponseMessage = await message.SendAsync(cancellationToken, verb).ConfigureAwait(false))
						response = (await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) ?? "{}").ToJson();
				}
				catch (Exception ex)
				{
					status = "Error";
					var additional = "";
					if (ex is RemoteServerException rse)
					{
						try
						{
							response = (rse.Body ?? "{}").ToJson();
						}
						catch { }
						additional = $"\r\n\r\nError: {response?.ToString(jsonFormat)}";
					}
					await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(ex, $"Error occurred while processing a pre-message when forward a web-hook message => {ex.Message}\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nMessage: {message.AsJson.ToString(jsonFormat)}{additional}")).ConfigureAwait(false);
				}

				messageJson["PreRequest"] = new JObject
				{
					["Status"] = status,
					["URL"] = message.EndpointURL,
					["Header"] = message.Header.ToJObject(),
					["Query"] = message.Query.ToJObject(),
					["Body"] = message.Body.ToJson(),
					["Response"] = response
				};

				if (writeLogs)
					debugLogs += $"PRE-PREPARE STEP ------:\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nPre-prepared message: {message.AsJson}\r\n\r\nUpdated message: {messageJson}\r\n\r\n";
			}

			JToken result = null;
			try
			{
				result = string.IsNullOrWhiteSpace(settings.PrepareBodyScript) ? messageJson : settings.PrepareBodyScript.JsEvaluate(messageJson, requestInfo.AsJson, paramsJson)?.ToString().ToJson();
				if (writeLogs)
					debugLogs += $"PREPARE STEP ------:\r\n\r\nPrepared message [{!string.IsNullOrWhiteSpace(settings.PrepareBodyScript)}]: {result}\r\n\r\n";
			}
			catch (Exception ex)
			{
				await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(ex, $"Error occurred while preparing a web-hook message to forward => {ex.Message}\r\n\r\nMessage: {messageJson.ToString(jsonFormat)}")).ConfigureAwait(false);
				throw;
			}

			verb = result?.Get<string>("Verb") ?? "POST";
			var body = result?.Get<JObject>("Body") ?? new JObject();
			var endpointURLs = result?.Get<JArray>("EndpointURLs")?.ToList<string>() ?? new List<string>();
			endpointURLs.Add(result?.Get<string>("EndpointURL"));
			endpointURLs = endpointURLs.Select(endpointURL => endpointURL?.Trim()).Where(endpointURL => !string.IsNullOrWhiteSpace(endpointURL) && (endpointURL.IsStartsWith("https://") || endpointURL.IsStartsWith("http://"))).ToList();

			message = new WebHookMessage
			{
				EndpointURL = "https://apis.vieapps.net/webhooks",
				Header = result?.Get<JObject>("Header")?.ToDictionary<string>(),
				Query = result?.Get<JObject>("Query")?.ToDictionary<string>(),
				Body = body.ToString(Formatting.None)
			}.Normalize(settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, webhookQuery?.ToDictionary<string>(), webhookHeader?.ToDictionary<string>(), encryptionKey, encryptionIV);
			if (!string.IsNullOrWhiteSpace(secretToken))
				message.Header[string.IsNullOrWhiteSpace(secretTokenName) ? "x-webhook-secret-token" : secretTokenName] = secretToken;

			var responses = new JArray();
			await endpointURLs.ForEachAsync(async endpointURL =>
			{
				var status = "OK";
				JToken response = null;
				message.EndpointURL = endpointURL;

				try
				{
					using (var httpResponseMessage = await message.SendAsync(cancellationToken, verb).ConfigureAwait(false))
						response = (await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) ?? "{}").ToJson();
				}
				catch (Exception ex)
				{
					status = "Error";
					var additional = "";
					if (ex is RemoteServerException rse)
					{
						try
						{
							response = (rse.Body ?? "{}").ToJson();
						}
						catch { }
						additional = $"\r\n\r\nError: {response?.ToString(jsonFormat)}";
					}
					await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(ex, $"Error occurred while forwarding a web-hook message => {ex.Message}\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nMessage: {message.AsJson.ToString(jsonFormat)}{additional}")).ConfigureAwait(false);
				}

				responses.Add(new JObject
				{
					["Status"] = status,
					["URL"] = message.EndpointURL,
					["Response"] = response
				});

				if (writeLogs)
					debugLogs += $"FORWARD STEP ------:\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nMessage: {message.AsJson}\r\n\r\nResponse: {response}\r\n\r\n";
			}, true, false).ConfigureAwait(false);
			result = responses;

			if (message.Header.TryGetValue("x-webhook-post-endpoint-url", out var postEndpointURL))
			{
				body["Responses"] = responses;
				verb = message.Header.TryGetValue("x-webhook-post-verb", out var postVerb) ? postVerb : "POST";

				message = new WebHookMessage
				{
					EndpointURL = postEndpointURL,
					Header = message.Header.GetDictionary("x-webhook-post-header") ?? message.Header,
					Query = message.Header.GetDictionary("x-webhook-post-query") ?? message.Query,
					Body = body.ToString(Formatting.None),
				}.Normalize(settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, webhookQuery?.ToDictionary<string>(), webhookHeader?.ToDictionary<string>(), encryptionKey, encryptionIV);
				if (!string.IsNullOrWhiteSpace(secretToken))
					message.Header[string.IsNullOrWhiteSpace(secretTokenName) ? "x-webhook-secret-token" : secretTokenName] = secretToken;

				try
				{
					using (var httpResponseMessage = await message.SendAsync(cancellationToken, verb).ConfigureAwait(false))
					{
						result = (await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) ?? "{}").ToJson();
						if (writeLogs)
							debugLogs += $"POST-FORWARD STEP ------:\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nMessage: {message.AsJson}\r\n\r\nResponse: {result}\r\n\r\n";
					}
				}
				catch (Exception ex)
				{
					result = responses;
					var additional = ex is RemoteServerException rse ? $"\r\n\r\nError: {(rse.Body ?? "{}").ToJson().ToString(jsonFormat)}" : "";
					await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(ex, $"Error occurred while processing a post-message when forward a web-hook message => {ex.Message}\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nMessage: {message.AsJson.ToString(jsonFormat)}{additional}")).ConfigureAwait(false);
				}
			}

			if (writeLogs)
				debugLogs = $"\r\n\r\nINIT STEP ------:\r\n\r\nMessage: {requestInfo.AsJson}\r\n\r\n" + debugLogs;
			await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(null, $"Forward a web-hook message successful [{requestInfo.Header["x-webhook-uri"]}]{debugLogs}")).ConfigureAwait(false);

			return result;
		}

		/// <summary>
		/// Sends a web-hook message as call the destination service
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="preparer"></param>
		/// <returns></returns>
		public static Task<JToken> SendAsCallServiceAsync(this WebHookMessage message, CancellationToken cancellationToken = default, Action<RequestInfo> preparer = null)
		{
			if (string.IsNullOrWhiteSpace(message.EndpointURL) || string.IsNullOrWhiteSpace(message.Body))
				return Task.FromException<JToken>(new MessageException("Invalid (end-point/body)"));

			var path = new Uri(message.EndpointURL).PathAndQuery;
			var pos = path.IndexOf("?");
			path = pos > 0 ? path.Left(pos) : path;
			while (path.StartsWith("/"))
				path = path.Right(path.Length - 1);
			while (path.EndsWith("/"))
				path = path.Left(path.Length - 1);
			var pathSegments = path.ToArray("/");

			var requestInfo = new RequestInfo
			{
				ServiceName = pathSegments.Length > 1 && !string.IsNullOrWhiteSpace(pathSegments[1]) ? pathSegments[1].GetANSIUri(false, true).GetCapitalizedFirstLetter() : "",
				ObjectName = "",
				Verb = "POST",
				Query = message.Query,
				Header = message.Header,
				Body = message.Body,
				CorrelationID = message.CorrelationID
			};
			requestInfo.Header["x-webhook-service"] = requestInfo.ServiceName;
			if (pathSegments.Length > 2 && !string.IsNullOrWhiteSpace(pathSegments[2]))
				requestInfo.Header["x-webhook-system"] = pathSegments[2].GetANSIUri();
			if (pathSegments.Length > 3 && !string.IsNullOrWhiteSpace(pathSegments[3]))
			{
				if (pathSegments[3].GetANSIUri().IsValidUUID())
					requestInfo.Header["x-webhook-entity"] = pathSegments[3].GetANSIUri();
				else
					requestInfo.Header["x-webhook-object"] = pathSegments[3].GetANSIUri(false, true).Replace("-", "").Replace("_", "");
			}
			if (pathSegments.Length > 4 && !string.IsNullOrWhiteSpace(pathSegments[4]))
				requestInfo.Header["x-webhook-adapter"] = pathSegments[4].GetANSIUri().Replace("-", "").Replace("_", "");

			preparer?.Invoke(requestInfo);
			return requestInfo.GetService().ProcessWebHookMessageAsync(requestInfo, cancellationToken);
		}

	}
}