#region Related components
using System;
using System.Collections.Specialized;
using System.Dynamic;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents the information of a request to a service
	/// </summary>
	[Serializable]
	public class RequestInfo
	{
		public RequestInfo()
		{
			this.Session = new Session();
			this.ServiceName = "";
			this.ObjectName = "";
			this.Verb = "GET";
			this.Query = new NameValueCollection();
			this.Header = new NameValueCollection();
			this.Body = "";
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
		public NameValueCollection Query { get; set; }

		/// <summary>
		/// Gets or sets the header
		/// </summary>
		public NameValueCollection Header { get; set; }

		/// <summary>
		/// Gets or sets the JSON body of the request (only available when verb is POST or PUT)
		/// </summary>
		public string Body { get; set; }
		#endregion

		#region Helper methods
		/// <summary>
		/// Gets the request body in JSON
		/// </summary>
		/// <returns></returns>
		public JObject GetJsonBody()
		{
			return string.IsNullOrWhiteSpace(this.Body)
				? null
				: JObject.Parse(this.Body);
		}

		/// <summary>
		/// Gets the request body in dynamic object (Expando)
		/// </summary>
		/// <returns></returns>
		public ExpandoObject GetExpandoBody()
		{
			return string.IsNullOrWhiteSpace(this.Body)
				? null
				: this.Body.ToExpandoObject();
		}

		/// <summary>
		/// Gets the meta info with two steps: first from header, second from query
		/// </summary>
		/// <param name="name">The string that presents name of parameter want to get</param>
		/// <returns></returns>
		public string GetMeta(string name)
		{
			var info = this.Header != null
				? this.Header[name]
				: null;

			if (string.IsNullOrWhiteSpace(info))
				info = this.Query != null
					? this.Query[name]
					: null;

			return info;
		}

		/// <summary>
		/// Gets the identity of the device that send this request
		/// </summary>
		/// <returns></returns>
		public string GetDeviceID()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.DeviceID)
				? this.Session.DeviceID
				: this.GetMeta("x-device-id");
		}

		/// <summary>
		/// Gets the name of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppName()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppName)
				? this.Session.AppName
				: this.GetMeta("x-app-name");
		}

		/// <summary>
		/// Gets the platform of the app that send this request
		/// </summary>
		/// <returns></returns>
		public string GetAppPlatform()
		{
			return this.Session != null && !string.IsNullOrWhiteSpace(this.Session.AppPlatform)
				? this.Session.AppPlatform
				: this.GetMeta("x-app-platform");
		}
		#endregion

	}

}