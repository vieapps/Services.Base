#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using JSPool;
using JavaScriptEngineSwitcher.Core;
using JavaScriptEngineSwitcher.ChakraCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		static JsPool JsEnginePool { get; }

		static Extensions()
		{
			JsEngineSwitcher.Current.DefaultEngineName = ChakraCoreJsEngine.EngineName;
			JsEngineSwitcher.Current.EngineFactories.AddChakraCore(new ChakraCoreSettings
			{
				DisableEval = true,
				EnableExperimentalFeatures = true
			});
			Extensions.JsEnginePool = new JsPool(new JsPoolConfig
			{
				MaxEngines = UtilityService.GetAppSetting("JsEngine:MaxEngines", "25").As<int>(),
				MaxUsagesPerEngine = UtilityService.GetAppSetting("JsEngine:MaxUsagesPerEngine", "100").As<int>(),
				GetEngineTimeout = TimeSpan.FromSeconds(UtilityService.GetAppSetting("JsEngine:GetEngineTimeout", "3").As<int>())
			});
		}

		static Func<DateTime> Func_Now => () => DateTime.Now;

		static Func<string, bool, string> Func_GenerateURI => (name, lowerCase) => name.GetANSIUri(lowerCase);

		static Func<string, string> Func_GenerateID => value => UtilityService.GenerateUUID(value);

		static Func<string, string> Func_ToJSON => value => new RequestInfo { Body = value }.BodyAsJson.ToString(Formatting.None);

		static Func<string, string> Func_ToHex => value => EncodingService.ToHex(value);

		static Func<string, string> Func_ToBase64 => value => EncodingService.ToBase64(value);

		static Func<string, string> Func_ToBase64Url => value => EncodingService.ToBase64Url(value);

		static Func<string, string> Func_EncodeBase64 => value => EncodingService.ToBase64(value);

		static Func<string, string> Func_DecodeBase64 => value => EncodingService.FromBase64(value);

		static Func<string, string> Func_EncodeBase64Url => value => EncodingService.Url64Encode(value);

		static Func<string, string> Func_DecodeBase64Url => value => EncodingService.Url64Decode(value);

		static Func<string, string, string> Func_Hash => (value, algorithm) => CryptoService.GetHash(value, algorithm).GetString();

		static Func<string, string, string, string> Func_Hmac => (value, key, algorithm) => CryptoService.GetHMAC(value, key, algorithm);

		static Func<string, string> Func_GetLocation => ipAddress =>
		{
			var location = Extensions.GetLocationAsync(null, ipAddress, UtilityService.NewUUID, ServiceBase.ServiceComponent.CancellationToken);
			location.Wait();
			return location.Result;
		};

		static Action<string, string> Func_WriteLogs => (correlationID, logs) =>
		{
			ServiceBase.ServiceComponent.WriteLogsAsync(correlationID, null, null, ServiceBase.ServiceComponent.Logger, new List<string> { logs }, null, ServiceBase.ServiceComponent.ServiceName, "WebHooks").Run();
		};

		static Func<string, string, string> Func_CallService => (service, requestInfo) =>
		{
			try
			{
				var request = requestInfo.ToJson();
				var callingTask = Router.GetService(service).ProcessRequestAsync(new RequestInfo
				{
					Session = new Session(request.Get<JObject>("Session")),
					ServiceName = request.Get<string>("ServiceName") ?? service,
					ObjectName = request.Get<string>("ObjectName"),
					Verb = request.Get<string>("Verb") ?? "GET",
					Query = request.Get<JObject>("Query")?.ToDictionary(token => token.ToString()),
					Header = request.Get<JObject>("Header")?.ToDictionary(token => token.ToString()),
					Body = request.Get<JObject>("Body")?.ToString(Formatting.None),
					Extra	= request.Get<JObject>("Extra")?.ToDictionary(token => token.ToString()),
					CorrelationID = request.Get<string>("CorrelationID")
				}, ServiceBase.ServiceComponent.CancellationToken);
				callingTask.Wait();
				return new JObject
				{
					["status"] = "OK",
					["code"] = 200,
					["response"] = callingTask.Result.ToString(Formatting.None)
				}.ToString(Formatting.None);
			}
			catch (Exception ex)
			{
				return ex is RemoteServerException rse
					? new JObject
					{
						["status"] = "Error",
						["code"] = (int)rse.StatusCode,
						["response"] = rse.Body,
						["stack"] = rse.StackTrace
					}.ToString(Formatting.None)
					: new JObject
					{
						["status"] = "Error",
						["code"] = 500,
						["response"] = ex.Message,
						["stack"] = ex.StackTrace
					}.ToString(Formatting.None);
			}
		};

		static Action<string, string, string, string> Func_SendCommunicateMessage => (service, type, data, excludedNodeID) =>
		{
			new CommunicateMessage(service)
			{
				Type = type,
				Data = data.ToJson(),
				ExcludedNodeID = excludedNodeID
			}.Send();
		};

		static Action<string, string, string, string> Func_SendUpdateMessage => (type, data, deviceID, excludedDeviceID) =>
		{
			new UpdateMessage
			{
				Type = type,
				Data = data.ToJson(),
				DeviceID = deviceID,
				ExcludedDeviceID = excludedDeviceID
			}.Send();
		};

		static Action<string, string> Func_SendEmail => (email, server) =>
		{
			var smtp = (server ?? "{}").ToJson();
			var fromEmail = smtp.Get("from_email", "");
			var fromName = smtp.Get("from_name", "");
			var data = (email ?? "{}").ToJson();
			ServiceBase.ServiceComponent.SendEmailAsync
			(
				data.Get("from", string.IsNullOrWhiteSpace(fromEmail) ? "" : string.IsNullOrWhiteSpace(fromName) ? fromEmail : $"{fromName} <{fromEmail}>"),
				data.Get("replyTo", ""),
				data.Get("to", ""),
				data.Get("cc", ""),
				data.Get("bcc", ""),
				data.Get("subject", ""),
				data.Get("body", ""),
				smtp.Get("host", ""),
				smtp.Get("port", 0),
				smtp.Get("ssl", true),
				smtp.Get("username", ""),
				smtp.Get("password", ""),
				ServiceBase.ServiceComponent.CancellationToken
			).Run();
		};

		static Func<string, string, string, string, string> Func_SendHttp => (url, method, body, header) =>
		{
			System.Net.Http.HttpResponseMessage response = null;
			try
			{
				var sendTask = new Uri(url).SendHttpRequestAsync(method ?? "GET", (header?.ToJson() as JObject)?.ToDictionary(token => token.ToString()), body, 90, ServiceBase.ServiceComponent.CancellationToken);
				sendTask.Wait();
				response = sendTask.Result;
				var readTask = response.ReadAsStringAsync();
				readTask.Wait();
				return readTask.Result;
			}
			catch
			{
				return null;
			}
			finally
			{
				response?.Dispose();
			}
		};

		/// <summary>
		/// Gets the common Javascript functions
		/// </summary>
		public static string JsFunctions { get; } = @"
		var __now = function() {
			return new Date().toJSON();
		};
		var __today = function() {
			var date = __now();
			return date.substring(0, date.indexOf('T')).replace(/\-/g, '/');
		};
		var __generateURI = function(value, lowerCase) {
			return __sf_GenerateURI(value, lowerCase !== undefined ? !!lowerCase : true);
		};
		var __generateID = function(value) {
			return __sf_GenerateID(value);
		};
		var __toQuery = function(params) {
			return Object.keys(params).map(key => {
				var value = params[key];
				return value === undefined ? undefined : key + (!!value ? `=${value}` : '');
			}).filter(value => value !== undefined).join('&');
		};
		var __toJSON = function(params) {
			return JSON.parse(__sf_ToJSON(params));
		};
		var __toDateTime = function(value) {
			if (value !== undefined) {
				if (value instanceof Date || (typeof value === 'string' && value.trim() !== '')) {
					var date = new Date(value);
					return new DateTime(date.getFullYear(), date.getMonth(), date.getDate(), date.getHours(), date.getMinutes(), date.getSeconds(), date.getMilliseconds());
				}
				return typeof value === 'number' ? new DateTime(value) : new DateTime();
			}
			return new DateTime();
		};
		var __toHex = function(value) {
			return __sf_ToHex(value);
		};
		var __toBase64 = function(value) {
			return __sf_ToBase64(value);
		};
		var __toBase64Url = function(value) {
			return __sf_ToBase64Url(value);
		};
		var __encodeBase64 = function(value) {
			return __sf_EncodeBase64(value);
		};
		var __decodeBase64 = function(value) {
			return __sf_DecodeBase64(value);
		};
		var __encodeBase64Url = function(value) {
			return __sf_EncodeBase64Url(value);
		};
		var __decodeBase64Url = function(value) {
			return __sf_DecodeBase64Url(value);
		};
		var __hash = function(value, algorithm) {
			return __sf_Hash(value, algorithm);
		};
		var __hmac = function(value, key, algorithm) {
			return __sf_Hmac(value, key, algorithm);
		};
		var __getLocation = function(value) {
			return __sf_GetLocation(value);
		};
		var __writeLogs = function(correlationID, logs) {
			__sf_WriteLogs(correlationID, logs);
		};
		var __callService = function(service, requestInfo) {
			return __sf_CallService(service, requestInfo);
		};
		var __sendCommunicateMessage = function(service, type, data, excludedNodeID) {
			__sf_SendCommunicateMessage(service, type, data, excludedNodeID || '');
		};
		var __sendUpdateMessage = function(type, data, deviceID, excludedDeviceID) {
			__sf_SendUpdateMessage(type, data, deviceID || '*', excludedDeviceID || '');
		};
		var __sendEmail = function(email, server) {
			__sf_SendEmail(email, server);
		};
		var __sendHttp = function(url, method, body, header) {
			return __sf_SendHttp(url, method, body, header);
		};
		var __getHttp = function(url, header) {
			return __sendHttp(url, 'GET', '', header || '{}');
		};
		var __postHttp = function(url, body, header) {
			return __sendHttp(url, 'POST', body || '', header || '{}');
		};
		".Replace("\t", "").Replace("\r", "").Replace("\n", " ");

		/// <summary>
		/// Casts the returning value of an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsValue"></param>
		/// <returns></returns>
		public static T JsCast<T>(object jsValue)
			=> jsValue == null || jsValue is Undefined
				? default
				: jsValue is string @string && typeof(T).Equals(typeof(DateTime)) && @string.Contains("T") && @string.Contains("Z") && DateTime.TryParse(@string, out var datetime)
					? datetime.As<T>()
					: jsValue.As<T>();

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, JToken @object, JToken requestInfo = null, JToken @params = null)
		{
			expression = !string.IsNullOrWhiteSpace(expression) && expression.StartsWith("@[") && expression.EndsWith("]")
				? expression.Left(expression.Length - 1).Substring(2).Trim()
				: (expression ?? "").Trim();
			return Extensions.JsFunctions
				+ Environment.NewLine
				+ $"var __object = {@object?.ToString(Formatting.None) ?? "{}"};"
				+ Environment.NewLine
				+ "__object.__evaluate = function (__request, __params) {"
				+ @"
				var __format = function(template, params) {
					if (!!!params) {
						params = {};
						Object.assign(params, __params || {});
						Object.assign(params, __request.Query);
						Object.assign(params, __request.Header);
						Object.assign(params, __request.Body);
					}
					Object.keys(params).forEach(key => {
						template = template.replace(new RegExp(`\\{\\{${key}\\}\\}`, 'g'), (params[key] || '').toString());
					});
					return template;
				};
				var __query = names => {
					var parameters = names.map(name => {
						var key = 'x-' + name;
						var value = __request.Query[key];
						return value === undefined ? undefined : key + (!!value ? `=${value}` : '');
					}).filter(value => value !== undefined).join('&');
					return !!parameters ? '&' + parameters : '';
				};
				".Replace("\t\t\t\t", "")
				+ (string.IsNullOrWhiteSpace(expression) || expression.Equals(";") ? "return undefined;" : $"{(expression.IndexOf("return") < 0 ? "return " : "")}{expression}{(expression.EndsWith(";") ? "" : ";")}")
				+ Environment.NewLine
				+ "};"
				+ Environment.NewLine
				+ $"__object.__evaluate({requestInfo?.ToString(Formatting.None) ?? "{}"}, {@params?.ToString(Formatting.None) ?? "{}"});";
		}

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null)
			=> expression?.GetJsExpression(@object?.ToJson(), requestInfo?.ToJson(), @params?.ToJson());

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null)
			=> expression?.GetJsExpression(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value - supported types: Undefined, Boolean, Int, Double, String</returns>
		public static object JsEvaluate(this string expression, JToken @object, JToken requestInfo = null, JToken @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			if (!string.IsNullOrWhiteSpace(expression))
				using (var jsEngine = Extensions.JsEnginePool.GetEngine())
				{
					var objects = new Dictionary<string, object>(embedObjects ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase)
					{
						["__sf_Now"] = Extensions.Func_Now,
						["__sf_GenerateURI"] = Extensions.Func_GenerateURI,
						["__sf_GenerateID"] = Extensions.Func_GenerateID,
						["__sf_ToJSON"] = Extensions.Func_ToJSON,
						["__sf_ToHex"] = Extensions.Func_ToHex,
						["__sf_ToBase64"] = Extensions.Func_ToBase64,
						["__sf_ToBase64Url"] = Extensions.Func_ToBase64Url,
						["__sf_EncodeBase64"] = Extensions.Func_EncodeBase64,
						["__sf_DecodeBase64"] = Extensions.Func_DecodeBase64,
						["__sf_EncodeBase64Url"] = Extensions.Func_EncodeBase64Url,
						["__sf_DecodeBase64Url"] = Extensions.Func_DecodeBase64Url,
						["__sf_Hash"] = Extensions.Func_Hash,
						["__sf_Hmac"] = Extensions.Func_Hmac,
						["__sf_GetLocation"] = Extensions.Func_GetLocation,
						["__sf_WriteLogs"] = Extensions.Func_WriteLogs,
						["__sf_CallService"] = Extensions.Func_CallService,
						["__sf_SendCommunicateMessage"] = Extensions.Func_SendCommunicateMessage,
						["__sf_SendUpdateMessage"] = Extensions.Func_SendUpdateMessage,
						["__sf_SendEmail"] = Extensions.Func_SendEmail,
						["__sf_SendHttp"] = Extensions.Func_SendHttp,
					};
					objects.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostObject(kvp.Key, kvp.Value));
					var types = new Dictionary<string, Type>(embedTypes ?? new Dictionary<string, Type>(), StringComparer.OrdinalIgnoreCase)
					{
						["Uri"] = typeof(Uri),
						["DateTime"] = typeof(DateTime),
					};
					types.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostType(kvp.Key, kvp.Value));
					var jsValue = jsEngine.Evaluate(expression.GetJsExpression(@object, requestInfo, @params));
					return jsValue is Undefined ? null : jsValue;
				}
			return null;
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value - supported types: Undefined, Boolean, Int, Double, String</returns>
		public static object JsEvaluate(this string expression, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> expression?.JsEvaluate(@object?.ToJson(), requestInfo?.ToJson(), @params?.ToJson(), embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value - supported types: Undefined, Boolean, Int, Double, String</returns>
		public static object JsEvaluate(this string expression, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> expression?.JsEvaluate(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params, embedObjects, embedTypes);
	}
}