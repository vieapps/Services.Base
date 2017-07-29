#region Related components
using System;

using Newtonsoft.Json.Linq;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a base-message for updating via RTU (Real-Time Update)
	/// </summary>
	[Serializable]
	public class BaseMessage
	{
		public BaseMessage()
		{
			this.Type = "";
			this.Data = new JObject();
		}

		#region Properties
		/// <summary>
		/// Gets or sets type of update message
		/// </summary>
		public string Type { get; set; }

		/// <summary>
		/// Gets or sets data of update message
		/// </summary>
		public JToken Data { get; set; }
		#endregion

	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a message for updating via RTU (Real-Time Update)
	/// </summary>
	[Serializable]
	public class UpdateMessage : BaseMessage
	{
		public UpdateMessage() : base()
		{
			this.DeviceID = "";
			this.ExcludedDeviceID = "";
		}

		#region Properties
		/// <summary>
		/// Gets or sets identity of device that received the message
		/// </summary>
		public string DeviceID { get; set; }

		/// <summary>
		/// Gets or sets the identity of excluded devices
		/// </summary>
		public string ExcludedDeviceID { get; set; }
		#endregion

	}

}