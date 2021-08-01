#region Related components
using System;
using System.Dynamic;
using System.Collections.Generic;
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
			=> requestInfo?.BodyAsJson ?? new JObject();

		/// <summary>
		/// Gets the request body in dynamic object (ExpandoObject)
		/// </summary>
		/// <returns></returns>
		public static ExpandoObject GetBodyExpando(this RequestInfo requestInfo)
			=> requestInfo?.BodyAsExpandoObject ?? new ExpandoObject();

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
			=> requestInfo?.GetQueryParameter("x-request")?.Url64Decode()?.ToExpandoObject() ?? new ExpandoObject();

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
		/// Validates a web-hook message
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="signAlgorithm"></param>
		/// <param name="signKey"></param>
		/// <param name="signatureName"></param>
		/// <param name="signatureAsHex"></param>
		/// <param name="signatureInQuery"></param>
		/// <param name="query"></param>
		/// <param name="header"></param>
		public static void ValidateWebHookMessage(this RequestInfo requestInfo, string signAlgorithm, string signKey, string signatureName, bool signatureAsHex = true, bool signatureInQuery = false, IDictionary<string, string> query = null, IDictionary<string, string> header = null)
		{
			if (string.IsNullOrWhiteSpace(signAlgorithm) || !CryptoService.HmacHashAlgorithmFactories.ContainsKey(signAlgorithm))
				signAlgorithm = "SHA256";

			var signature = "";
			using (var hasher = CryptoService.GetHMACHashAlgorithm((signKey ?? CryptoService.DEFAULT_PASS_PHRASE).ToBytes(), signAlgorithm))
			{
				var body = (requestInfo.Body ?? "").ToBytes();
				signature = signatureAsHex
					? hasher.ComputeHash(body).ToHex()
					: hasher.ComputeHash(body).ToBase64();
			}

			if (string.IsNullOrWhiteSpace(signatureName))
				signatureName = $"Hmac{signAlgorithm.GetCapitalizedFirstLetter()}Signature";

			var signatureOfMessage = signatureInQuery
				? requestInfo.Query != null && requestInfo.Query.ContainsKey(signatureName) ? requestInfo.Query[signatureName] : ""
				: requestInfo.Header != null && requestInfo.Header.ContainsKey(signatureName) ? requestInfo.Header[signatureName] : "";

			if (!signature.IsEquals(signatureOfMessage))
				throw new InformationInvalidException("Invalid (signature)");

			if (query != null && query.Count > 0)
				foreach (var kvp in query)
				{
					var key = kvp.Key;
					var value = kvp.Value;
					if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !value.IsEquals(requestInfo.Query != null && requestInfo.Query.ContainsKey(key) ? requestInfo.Query[key] : ""))
						throw new InformationInvalidException("Invalid (query)");
				}

			if (header != null && header.Count > 0)
				foreach (var kvp in header)
				{
					var key = kvp.Key;
					var value = kvp.Value;
					if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !value.IsEquals(requestInfo.Header != null && requestInfo.Header.ContainsKey(key) ? requestInfo.Header[key] : ""))
						throw new InformationInvalidException("Invalid (header)");
				}
		}
	}
}