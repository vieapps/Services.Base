#region Related components
using System;
using System.Collections.Generic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a session
	/// </summary>
	[Serializable]
	public class Session
	{
		public Session()
		{
			this.SessionID = "";
			this.DeviceID = "";
			this.IP = "";
			this.AppName = "";
			this.AppPlatform = "";
			this.AppAgent = "";
			this.AppOrigin = "";
			this.User = new User();
		}

		#region Properties
		/// <summary>
		/// Gets or sets the identity of session
		/// </summary>
		public string SessionID { get; set; }

		/// <summary>
		/// Gets or sets the device's identity (Device UUID) that associates with this session
		/// </summary>
		public string DeviceID { get; set; }

		/// <summary>
		/// Gets or sets the IP address of client's device
		/// </summary>
		public string IP { get; set; }

		/// <summary>
		/// Gets or sets the name of the the app that associates with this session
		/// </summary>
		public string AppName { get; set; }

		/// <summary>
		/// Gets or sets the name of the platform of the app that associates with this session
		/// </summary>
		public string AppPlatform { get; set; }

		/// <summary>
		/// Gets or sets the agent info (agent string) of the app that associates with this session
		/// </summary>
		public string AppAgent { get; set; }

		/// <summary>
		/// Gets or sets the origin info (origin or refer url) of the app that associates with this session
		/// </summary>
		public string AppOrigin { get; set; }

		/// <summary>
		/// Gets or sets the information of user who performs the action in the sesssion
		/// </summary>
		public User User { get; set; }
		#endregion

	}

}