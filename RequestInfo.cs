#region Related components
using System;
using System.Collections.Generic;
using System.Dynamic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents the requesting information of a service
	/// </summary>
	[Serializable]
	public class RequestInfo
	{
		/// <summary>
		/// Initializes a requesting information
		/// </summary>
		/// <param name="session"></param>
		/// <param name="serviceName"></param>
		/// <param name="objectName"></param>
		/// <param name="verb"></param>
		/// <param name="query"></param>
		/// <param name="header"></param>
		/// <param name="body"></param>
		/// <param name="extra"></param>
		/// <param name="correlationID"></param>
		public RequestInfo(Session session = null, string serviceName = null, string objectName = null, string verb = null, Dictionary<string, string> query = null, Dictionary<string, string> header = null, string body = null, Dictionary<string, string> extra = null, string correlationID = null)
		{
			this.Session = new Session(session);
			this.ServiceName = !string.IsNullOrWhiteSpace(serviceName) ? serviceName : "unknown";
			this.ObjectName = !string.IsNullOrWhiteSpace(objectName) ? objectName : "unknown";
			this.Verb = !string.IsNullOrWhiteSpace(verb) ? verb : "GET";
			this.Query = new Dictionary<string, string>(query ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
			this.Header = new Dictionary<string, string>(header ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
			this.Body = !string.IsNullOrWhiteSpace(body) ? body : "";
			this.Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
			this.CorrelationID = !string.IsNullOrWhiteSpace(correlationID) ? correlationID : UtilityService.NewUUID;
		}

		#region Properties
		/// <summary>
		/// Gets or sets the session
		/// </summary>
		public Session Session { get; set; }

		/// <summary>
		/// Gets or sets the name of service
		/// </summary>
		public string ServiceName { get; set; }

		/// <summary>
		/// Gets or sets the name of service's object
		/// </summary>
		public string ObjectName { get; set; }

		/// <summary>
		/// Gets or sets the verb (GET/POST/PUT/DELETE)
		/// </summary>
		public string Verb { get; set; }

		/// <summary>
		/// Gets or sets the query
		/// </summary>
		public Dictionary<string, string> Query { get; set; }

		/// <summary>
		/// Gets or sets the header
		/// </summary>
		public Dictionary<string, string> Header { get; set; }

		/// <summary>
		/// Gets or sets the JSON body of the request (only available when verb is POST or PUT)
		/// </summary>
		public string Body { get; set; }

		/// <summary>
		/// Gets or sets the extra information
		/// </summary>
		public Dictionary<string, string> Extra { get; set; }

		/// <summary>
		/// Gets or sets the identity of the correlation
		/// </summary>
		public string CorrelationID { get; set; }
		#endregion

		#region Methods: get body & request
		/// <summary>
		/// Gets the request body in JSON
		/// </summary>
		/// <returns></returns>
		public JToken GetBodyJson() => string.IsNullOrWhiteSpace(this.Body) ? new JObject() : this.Body.ToJson();

		/// <summary>
		/// Gets the request body in dynamic object (Expando)
		/// </summary>
		/// <returns></returns>
		public ExpandoObject GetBodyExpando() => string.IsNullOrWhiteSpace(this.Body) ? new ExpandoObject() : this.Body.ToExpandoObject();

		/// <summary>
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to JSON
		/// </summary>
		/// <returns></returns>
		public JToken GetRequestJson() => (this.Query.ContainsKey("x-request") ? this.Query["x-request"].Url64Decode() : "{}").ToJson();

		/// <summary>
		/// Gets the value of the 'x-request' parameter of the query (in Base64Url) and converts to Expando
		/// </summary>
		/// <returns></returns>
		public ExpandoObject GetRequestExpando() => this.Query.ContainsKey("x-request") ? this.Query["x-request"].Url64Decode().ToExpandoObject() : new ExpandoObject();
		#endregion

		#region Methods: get parameters
		/// <summary>
		/// Gets the parameter from the header
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public string GetHeaderParameter(string name) => this.Header != null && !string.IsNullOrWhiteSpace(name) && this.Header.ContainsKey(name.ToLower()) ? this.Header[name.ToLower()] : null;

		/// <summary>
		/// Gets the parameter from the query
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public string GetQueryParameter(string name) => this.Query != null && !string.IsNullOrWhiteSpace(name) && this.Query.ContainsKey(name.ToLower()) ? this.Query[name.ToLower()] : null;

		/// <summary>
		/// Gets the parameter with two steps: first from header, then second step is from query if header has no value
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public string GetParameter(string name) => this.GetHeaderParameter(name) ?? this.GetQueryParameter(name);

		/// <summary>
		/// Gets the identity of the device that send this request
		/// </summary>
		/// <returns></returns>
		public string GetDeviceID() => this.Session != null && !string.IsNullOrWhiteSpace(this.Session.DeviceID) ? this.Session.DeviceID : this.GetParameter("x-device-id");

		/// <summary>
		/// Gets the name of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppName() => this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppName) ? this.Session.AppName : this.GetParameter("x-app-name");

		/// <summary>
		/// Gets the platform of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppPlatform() => this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppPlatform) ? this.Session.AppPlatform : this.GetParameter("x-app-platform");

		/// <summary>
		/// Gets the object identity (from the parameter named 'object-identity' of the query)
		/// </summary>
		/// <param name="requireUUID">true to require object identity is valid UUID</param>
		/// <returns></returns>
		public string GetObjectIdentity(bool requireUUID = false)
		{
			var objectIdentity = this.GetQueryParameter("object-identity");
			return !string.IsNullOrWhiteSpace(objectIdentity)
				? !requireUUID
					? objectIdentity
					: objectIdentity.IsValidUUID()
						? objectIdentity
						: null
				: null;
		}
		#endregion

		/// <summary>
		/// Gets the full URI of this request
		/// </summary>
		[JsonIgnore]
		public string URI
		{
			get
			{
				var uri = "/" + this.ServiceName;
				if (!string.IsNullOrWhiteSpace(this.ObjectName))
				{
					uri += "/" + this.ObjectName;
					var id = this.GetObjectIdentity();
					if (!string.IsNullOrWhiteSpace(id))
					{
						uri += "/" + id;
						if (!id.IsValidUUID() && this.Query != null && this.Query.ContainsKey("id"))
							uri += "/" + this.Query["id"];
					}
				}
				return uri.ToLower();
			}
		}
	}
}