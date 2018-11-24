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
	/// Presents a working session
	/// </summary>
	[Serializable]
	public class Session : ISession
	{
		/// <summary>
		/// Initializes a new session
		/// </summary>
		/// <param name="session"></param>
		public Session(ISession session = null)
		{
			this.SessionID = session?.SessionID ?? "";
			this.DeviceID = session?.DeviceID ?? "";
			this.IP = session?.IP ?? "";
			this.AppName = session?.AppName ?? "";
			this.AppPlatform = session?.AppPlatform ?? "";
			this.AppAgent = session?.AppAgent ?? "";
			this.AppOrigin = session?.AppOrigin ?? "";
			this.AppMode = session?.AppMode ?? "Client";
			this.User = session?.User ?? new User("", this.SessionID, new List<string>(), new List<Privilege>());
			this.Verification = session != null ? session.Verification : false;
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
		/// Gets or sets the mode of the app that associates with this session
		/// </summary>
		public string AppMode { get; set; }

		/// <summary>
		/// Gets or sets the information of user who performs the action in the sesssion
		/// </summary>
		public User User { get; set; }

		/// <summary>
		/// Gets or sets two-factors verification status
		/// </summary>
		public bool Verification { get; set; }
		#endregion

	}

}