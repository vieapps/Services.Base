#region Related components
using System;
using System.Net;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{

		#region Body & Request
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
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to JSON
		/// </summary>
		/// <returns></returns>
		public static JToken GetRequestJson(this RequestInfo requestInfo)
		{
			try
			{
				return (requestInfo?.GetQueryParameter("x-request")?.Url64Decode() ?? "{}").ToJson();
			}
			catch
			{
				return new JObject();
			}
		}

		/// <summary>
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to ExpandoObject
		/// </summary>
		/// <returns></returns>
		public static ExpandoObject GetRequestExpando(this RequestInfo requestInfo)
			=> requestInfo?.GetRequestJson()?.ToExpandoObject() ?? new ExpandoObject();
		#endregion

		#region Get parameters
		/// <summary>
		/// Gets the parameter from the header
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <param name="value"></param>
		/// <returns></returns>
		public static bool TryGetHeaderParameter(this RequestInfo requestInfo, string name, out string value)
		{
			value = null;
			return requestInfo != null && !string.IsNullOrWhiteSpace(name) && requestInfo.Header != null && requestInfo.Header.TryGetValue(name, out value);
		}

		/// <summary>
		/// Gets the parameter from the header
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetHeaderParameter(this RequestInfo requestInfo, string name)
			=> requestInfo != null
				? requestInfo.TryGetHeaderParameter(name, out var value) ? value : null
				: null;

		/// <summary>
		/// Gets the parameter from the query
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <param name="value"></param>
		/// <returns></returns>
		public static bool TryGetQueryParameter(this RequestInfo requestInfo, string name, out string value)
		{
			value = null;
			return requestInfo != null && !string.IsNullOrWhiteSpace(name) && requestInfo.Query != null && requestInfo.Query.TryGetValue(name, out value);
		}

		/// <summary>
		/// Gets the parameter from the query
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetQueryParameter(this RequestInfo requestInfo, string name)
			=> requestInfo != null
				? requestInfo.TryGetQueryParameter(name, out var value) ? value : null
				: null;

		/// <summary>
		/// Gets the parameter with two steps: first from header, then second step is from query if header has no value
		/// </summary>
		/// <param name="name"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public static bool TryGetParameter(this RequestInfo requestInfo, string name, out string value)
		{
			value = null;
			return requestInfo != null && (requestInfo.TryGetHeaderParameter(name, out value) || requestInfo.TryGetQueryParameter(name, out value));
		}

		/// <summary>
		/// Gets the parameter with two steps: first from header, then second step is from query if header has no value
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public static string GetParameter(this RequestInfo requestInfo, string name)
			=> requestInfo != null ? requestInfo.TryGetParameter(name, out var value) ? value : null : null;

		/// <summary>
		/// Checks the parameter is existed in header or query
		/// </summary>
		/// <param name="name"></param>
		/// <returns></returns>
		public static bool ContainsKey(this RequestInfo requestInfo, string name)
			=> (requestInfo.Header != null && requestInfo.Header.ContainsKey(name)) || (requestInfo.Query != null && requestInfo.Query.ContainsKey(name));
		#endregion

		#region Identities
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
		#endregion

		/// <summary>
		/// Gets the full URI
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="transformer"></param>
		/// <returns></returns>
		public static string GetURI(this RequestInfo requestInfo, bool includeEncodedRequest = false, Func<string, string> transformer = null)
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
						if (requestInfo.TryGetQueryParameter("x-request", out var request))
						{
							if (includeEncodedRequest)
								uri += $"?x-request={request}";
						}
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

		#region User profiles/sessions
		/// <summary>
		/// Gets profile of collection of users
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userIDs"></param>
		/// <param name="fetchSessions"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> GetUserProfilesAsync(this RequestInfo requestInfo, IEnumerable<string> userIDs, bool fetchSessions = true, CancellationToken cancellationToken = default)
		{
			var request = new RequestInfo(requestInfo.Session, "Users", "Profile", "GET")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", "fetch" },
					{ "x-request", new JObject { { "IDs", userIDs.ToJArray() } }.ToString(Formatting.None).Url64Encode() }
				},
				Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "x-notifications-key", UtilityService.GetAppSetting("Keys:Notifications", "") },
					{ "x-fetch-sessions", fetchSessions.ToString().ToLower() }
				},
				CorrelationID = requestInfo.CorrelationID
			};
			if (requestInfo.TryGetParameter("x-logs", out var debugLogs))
				request.Header["x-logs"] = debugLogs;
			if (requestInfo.TryGetParameter("x-force-cache", out var forceCache))
				request.Header["x-force-cache"] = forceCache;
			return request.CallServiceAsync(cancellationToken);
		}

		/// <summary>
		/// Gets the sessions of an user. 1st element is session identity, 2nd element is device identity, 3rd element is app info, 4th element is online status
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<List<(string SessionID, string DeviceID, string AppInfo, bool IsOnline)>> GetUserSessionsAsync(this RequestInfo requestInfo, string userID = null, CancellationToken cancellationToken = default)
		{
			var result = await new RequestInfo(requestInfo.Session, "Users", "Account", "HEAD")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", userID ?? requestInfo.Session.User.ID }
				},
				CorrelationID = requestInfo.CorrelationID
			}.CallServiceAsync(cancellationToken).ConfigureAwait(false);			
			return (result["Sessions"] as JArray).ToList(info => (info.Get<string>("SessionID"), info.Get<string>("DeviceID"), info.Get<string>("AppInfo"), info.Get<bool>("IsOnline")));
		}
		#endregion

		#region Notifications
		static HashSet<string> ExcludedInDetails { get; } = new[] { "Time", "Sender", "SenderID", "SenderName", "Recipients", "RecipientIDs", "RecipientID", "Action", "Event", "ServiceName", "ServiceName", "ObjectName", "SystemID", "RepositoryID", "RepositoryEntityID", "ObjectID", "Title", "ObjectTitle", "Status", "PreviousStatus", "Additionals" }.ToHashSet();

		static HashSet<string> ExcludedInBody { get; } = new[] { "Time", "Sender", "SenderID", "SenderName", "Recipients", "RecipientIDs", "RecipientID", "Action", "Event", "ServiceName", "ServiceName", "ObjectName", "Title", "ObjectTitle", "Additionals" }.ToHashSet();

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
			var additionals = new JObject();
			detail.ForEach(kvp =>
			{
				if (!ExcludedInDetails.Contains(kvp.Key))
					additionals[kvp.Key] = kvp.Value;
				else if (!ExcludedInBody.Contains(kvp.Key))
					body[kvp.Key] = kvp.Value;
			});
			detail["Additionals"] = additionals;

			// send the notification
			await new RequestInfo(requestInfo.Session, "Notifications", "Notification", "POST")
			{
				Body = body.ToString(Formatting.None),
				Extra = new Dictionary<string, string>(requestInfo.Extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
				{
					{ "x-notifications-key", UtilityService.GetAppSetting("Keys:Notifications", "") }
				},
				CorrelationID = requestInfo.CorrelationID
			}.CallServiceAsync(cancellationToken).ConfigureAwait(false);
		}
		#endregion

		#region WebHooks
		/// <summary>
		/// Converts this web-hook message to request object
		/// </summary>
		/// <param name="message"></param>
		/// <param name="request"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static RequestInfo ToRequestInfo(this WebHookMessage message, RequestInfo request = null, Action<RequestInfo> onCompleted = null)
		{
			var requestInfo = new RequestInfo(request)
			{
				Verb = "POST",
				Body = message.Body ?? request?.Body,
				Query = new Dictionary<string, string>(message.Query ?? request?.Query ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
				Header = new Dictionary<string, string>(message.Header ?? request?.Header ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
				CorrelationID = message.CorrelationID ?? request?.CorrelationID
			};
			onCompleted?.Invoke(requestInfo);
			return requestInfo;
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
		/// <param name="signaturePrefix"></param>
		/// <param name="signatureSuffix"></param>
		/// <param name="requiredQuery"></param>
		/// <param name="requiredHeader"></param>
		/// <param name="decryptionKey"></param>
		/// <param name="decryptionIV"></param>
		/// <param name="doValidation"></param>
		/// <returns></returns>
		public static WebHookMessage ToWebHookMessage(this RequestInfo requestInfo, string secretToken, string secretTokenName, string signAlgorithm, string signKey, bool signKeyIsHex, string signatureName, bool signatureAsHex, string signaturePrefix, string signatureSuffix, IDictionary<string, string> requiredQuery, IDictionary<string, string> requiredHeader, byte[] decryptionKey, byte[] decryptionIV, bool doValidation = true)
		{
			var message = new WebHookMessage
			{
				EndpointURL = requestInfo.Session?.AppOrigin ?? requestInfo.GetHeaderParameter("Origin"),
				Body = requestInfo.Body,
				Query = requestInfo.Query,
				Header = requestInfo.Header,
				CorrelationID = requestInfo.CorrelationID
			};
			return doValidation
				? message.Validate(secretToken, secretTokenName, signAlgorithm, signKey, signKeyIsHex, signatureName, signatureAsHex, signaturePrefix, signatureSuffix, requiredQuery, requiredHeader, decryptionKey, decryptionIV)
				: message;
		}

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
		public static async Task<JToken> ForwardAsWebHookMessageAsync(this RequestInfo requestInfo, WebHookInfo settings, JToken paramsJson = null, string secretToken = null, string secretTokenName = null, Func<Exception, string, Task> writeLogsAsync = null, CancellationToken cancellationToken = default)
		{
			var signKey = settings.SignKey ?? requestInfo.GetAppID() ?? requestInfo.GetDeveloperID();
			var webhookQuery = settings.QueryAsJson?.ToDictionary<string>();
			var webhookHeader = settings.HeaderAsJson?.ToDictionary<string>();
			var encryptionKey = settings.EncryptionKey?.HexToBytes();
			var encryptionIV = settings.EncryptionIV?.HexToBytes();

			var message = requestInfo.ToWebHookMessage(secretToken, secretTokenName, settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, settings.SignaturePrefix, settings.SignatureSuffix, webhookQuery, webhookHeader, encryptionKey, encryptionIV);
			var messageJson = new JObject
			{
				["Header"] = message.Header.ToJObject(),
				["Query"] = message.Query.ToJObject(),
				["Body"] = requestInfo.BodyAsJson
			};

			var verb = "POST";
			var debugLogs = "";
			var writeLogs = requestInfo.ContainsKey("x-logs");
			var jsonFormat = writeLogs ? Formatting.Indented : Formatting.None;

			if (message.Header.TryGetValue("x-webhook-pre-endpoint-url", out var preEndpointURL))
			{
				verb = message.Header.TryGetValue("x-webhook-pre-verb", out var preVerb) ? preVerb : "POST";
				var status = "OK";
				var code = (int)HttpStatusCode.OK;
				JToken response = null;

				message = new WebHookMessage
				{
					EndpointURL = preEndpointURL,
					Header = message.Header.GetDictionary("x-webhook-pre-header"),
					Query = message.Header.GetDictionary("x-webhook-pre-query"),
					Body = message.Header.GetValue("x-webhook-pre-body") ?? "{}",
				}.Normalize(secretToken, secretTokenName, settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, settings.SignaturePrefix, settings.SignatureSuffix, webhookQuery, webhookHeader, encryptionKey, encryptionIV);

				try
				{
					using (var httpResponseMessage = await message.SendAsync(cancellationToken, verb).ConfigureAwait(false))
						response = (await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) ?? "{}").ToJson();
				}
				catch (Exception ex)
				{
					status = "Error";
					code = (int)HttpStatusCode.InternalServerError;
					var additional = "";
					if (ex is RemoteServerException rse)
					{
						code = (int)rse.StatusCode;
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
					["Code"] = code,
					["URL"] = message.EndpointURL,
					["Request"] = new JObject
					{
						["Header"] = message.Header.ToJObject(),
						["Query"] = message.Query.ToJObject(),
						["Body"] = message.Body.ToJson()
					},
					["Response"] = response
				};

				if (writeLogs)
					debugLogs += $"PRE-PREPARE STEP ------:\r\n\r\n{verb}: {message.EndpointURL}\r\n\r\nPre-prepared message: {message.AsJson}\r\n\r\nUpdated message: {messageJson}\r\n\r\n";
			}

			JToken result = null;
			try
			{
				result = string.IsNullOrWhiteSpace(settings.PrepareBodyScript)
					? messageJson
					: settings.PrepareBodyScript.JsEvaluate(messageJson, requestInfo.AsJson, paramsJson)?.ToString().ToJson();
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
			}.Normalize(secretToken, secretTokenName, settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, settings.SignaturePrefix, settings.SignatureSuffix, webhookQuery, webhookHeader, encryptionKey, encryptionIV);

			var responses = new JArray();
			await endpointURLs.ForEachAsync(async endpointURL =>
			{
				var status = "OK";
				var code = (int)HttpStatusCode.OK;
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
					code = (int)HttpStatusCode.InternalServerError;
					var additional = "";
					if (ex is RemoteServerException rse)
					{
						code = (int)rse.StatusCode;
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
					["Code"] = code,
					["URL"] = message.EndpointURL,
					["Request"] = new JObject
					{
						["Header"] = message.Header.ToJObject(),
						["Query"] = message.Query.ToJObject(),
						["Body"] = message.Body.ToJson()
					},
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
					Header = message.Header.GetDictionary("x-webhook-post-header") ?? message.Header?.Copy("x-webhook-post-endpoint-url,x-webhook-post-verb,x-webhook-post-header,x-webhook-post-query".ToHashSet()),
					Query = message.Header.GetDictionary("x-webhook-post-query") ?? message.Query,
					Body = body.ToString(Formatting.None),
				}.Normalize(secretToken, secretTokenName, settings.SignAlgorithm, signKey, settings.SignKeyIsHex, settings.SignatureName, settings.SignatureAsHex, false, settings.SignaturePrefix, settings.SignatureSuffix, webhookQuery, webhookHeader, encryptionKey, encryptionIV);

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
			await (writeLogsAsync == null ? Task.CompletedTask : writeLogsAsync(null, $"Forward a web-hook message successful [{requestInfo.GetParameter("x-webhook-uri")}]{debugLogs}")).ConfigureAwait(false);

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
			var pos = path.PositionOf("?");
			path = pos > 0 ? path.Left(pos) : path;
			while (path.IsStartsWith("/"))
				path = path.Right(path.Length - 1);
			while (path.IsEndsWith("/"))
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
		#endregion

		#region Session states
		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemIdentityJson"></param>
		/// <param name="serviceName"></param>
		/// <param name="serviceURI"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="onUpdateMessagePrepared"></param>
		/// <returns></returns>
		public static async Task<RequestInfo> SendSessionStateAsync(this RequestInfo requestInfo, JObject systemIdentityJson, string serviceName, string serviceURI, bool online, bool trackStatistics, bool sendClientMessage, Action<CommunicateMessage> onCommunicateMessagePrepared = null, Action<UpdateMessage> onUpdateMessagePrepared = null)
		{
			var systemID = systemIdentityJson?.Get<string>("ID");
			if (string.IsNullOrWhiteSpace(systemID) && requestInfo.ServiceName.IsStartsWith("Portals"))
				try
				{
					var body = requestInfo.Verb.IsEquals("POST") || requestInfo.Verb.IsEquals("PUT") || requestInfo.Verb.IsEquals("PATCH") ? requestInfo.BodyAsJson : null;
					systemID = body?.Get<string>("SystemID") ?? requestInfo.GetParameter("SystemID") ?? requestInfo.GetParameter("OrganizationID") ?? requestInfo.GetParameter("x-system-id");
					if (string.IsNullOrWhiteSpace(systemID))
					{
						systemID = requestInfo.GetParameter("active-id");
						if (string.IsNullOrWhiteSpace(systemID) && requestInfo.TryGetParameter("x-request", out var base64Request))
						{
							var request = base64Request.Url64Decode();
							var start = request.PositionOf("\"SystemID\":{\"Equals\":\"");
							if (start > 0)
							{
								start = request.PositionOf(":\"", start) + 2;
								var end = request.PositionOf("\"", start);
								systemID = request.Substring(start, end - start);
							}
						}
					}
				}
				catch { }
			await requestInfo.Session.SendSessionStateAsync((serviceName ?? requestInfo.ServiceName).ToLower(), serviceURI ?? $"{requestInfo.Verb} {requestInfo.GetURI()}", systemID, online, trackStatistics, sendClientMessage, onCommunicateMessagePrepared, onUpdateMessagePrepared, requestInfo.CorrelationID).ConfigureAwait(false);
			return requestInfo;
		}

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemIdentityJson"></param>
		/// <param name="serviceName"></param>
		/// <param name="serviceURI"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="onUpdateMessagePrepared"></param>
		public static void SendSessionState(this RequestInfo requestInfo, JObject systemIdentityJson, string serviceName, string serviceURI, bool online, bool trackStatistics, bool sendClientMessage, Action<CommunicateMessage> onCommunicateMessagePrepared = null, Action<UpdateMessage> onUpdateMessagePrepared = null)
			=> requestInfo.SendSessionStateAsync(systemIdentityJson, serviceName, serviceURI, online, trackStatistics, sendClientMessage, onCommunicateMessagePrepared, onUpdateMessagePrepared).Run();

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemIdentityJson"></param>
		/// <param name="onCommunicateMessagePrepared"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		public static void SendSessionState(this RequestInfo requestInfo, JObject systemIdentityJson, Action<CommunicateMessage> onCommunicateMessagePrepared, bool trackStatistics = true, bool sendClientMessage = false)
			=> requestInfo.SendSessionState(systemIdentityJson, null, null, true, trackStatistics, sendClientMessage, onCommunicateMessagePrepared, null);

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="online"></param>
		/// <param name="trackStatistics"></param>
		/// <param name="sendClientMessage"></param>
		public static void SendSessionState(this RequestInfo requestInfo, bool online, bool trackStatistics, bool sendClientMessage)
			=> requestInfo.SendSessionState(null, null, null, online, trackStatistics, sendClientMessage);

		/// <summary>
		/// Sends session state
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="sendClientMessage"></param>
		public static void SendSessionState(this RequestInfo requestInfo, bool trackStatistics = true, bool sendClientMessage = false)
			=> requestInfo.SendSessionState(true, trackStatistics, sendClientMessage);

		/// <summary>
		/// Sends tracking statistics
		/// </summary>
		/// <param name="requestInfo"></param>
		public static void TrackStatistics(this RequestInfo requestInfo)
			=> new CommunicateMessage("Users")
			{
				Type = "Statistics#Track",
				Data = new JObject
				{
					["SessionID"] = requestInfo.Session?.SessionID,
					["UserID"] = requestInfo.Session?.User?.ID,
					["CorrelationID"] = requestInfo.CorrelationID
				}
			}.Send();
		#endregion

	}
}