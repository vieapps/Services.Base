#region Related components
using System;
using System.Collections.Generic;
using System.Dynamic;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents the information of a request to a service
	/// </summary>
	[Serializable]
	public class RequestInfo
	{
		public RequestInfo(Session session = null, User user = null)
		{
			this.Session = session ?? new Session();
			if (session != null && user != null)
				this.Session.User = user;
			this.ServiceName = "";
			this.ObjectName = "";
			this.Verb = "GET";
			this.Query = new Dictionary<string, string>();
			this.Header = new Dictionary<string, string>();
			this.Body = "";
			this.Extra = new Dictionary<string, string>();
			this.CorrelationID = "";
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
		public JObject GetBodyJson()
		{
			return string.IsNullOrWhiteSpace(this.Body)
				? new JObject()
				: JObject.Parse(this.Body);
		}

		/// <summary>
		/// Gets the request body in dynamic object (Expando)
		/// </summary>
		/// <returns></returns>
		public ExpandoObject GetBodyExpando()
		{
			return string.IsNullOrWhiteSpace(this.Body)
				? new ExpandoObject()
				: this.Body.ToExpandoObject();
		}

		/// <summary>
		/// Gets the value of the 'request' parameter of the query (in Base64Url) and converts to JSON
		/// </summary>
		/// <returns></returns>
		public JObject GetRequestJson()
		{
			return this.Query.ContainsKey("request")
				? JObject.Parse(this.Query["request"].Url64Decode())
				: new JObject();
		}

		/// <summary>
		/// Gets the value of the 'request' parameter of the query (in Base64Url) and converts to Expando
		/// </summary>
		/// <returns></returns>
		public ExpandoObject GetRequestExpando()
		{
			return this.Query.ContainsKey("request")
				? this.Query["request"].Url64Decode().ToExpandoObject()
				: new ExpandoObject();
		}
		#endregion

		#region Methods: get parameters
		/// <summary>
		/// Gets the parameter with two steps: first from header, then second step is from query if header has no value
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public string GetParameter(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			var value = this.Header != null && this.Header.ContainsKey(name.ToLower())
				? this.Header[name.ToLower()]
				: null;

			if (string.IsNullOrWhiteSpace(value))
				value = this.Query != null && this.Query.ContainsKey(name.ToLower())
					? this.Query[name.ToLower()]
					: null;

			return value;
		}

		/// <summary>
		/// Gets the identity of the device that send this request
		/// </summary>
		/// <returns></returns>
		public string GetDeviceID()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.DeviceID)
				? this.Session.DeviceID
				: this.GetParameter("x-device-id");
		}

		/// <summary>
		/// Gets the name of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppName()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppName)
				? this.Session.AppName
				: this.GetParameter("x-app-name");
		}

		/// <summary>
		/// Gets the platform of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppPlatform()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppPlatform)
				? this.Session.AppPlatform
				: this.GetParameter("x-app-platform");
		}
		#endregion

	}
}