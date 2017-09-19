#region Related components
using System;

using Newtonsoft.Json.Linq;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a message for updating information
	/// </summary>
	[Serializable]
	public class BaseMessage
	{
		public BaseMessage()
		{
			this.Type = "";
			this.Data = new JObject();
		}

		/// <summary>
		/// Gets or sets type of update message
		/// </summary>
		public string Type { get; set; }

		/// <summary>
		/// Gets or sets data of update message
		/// </summary>
		public JToken Data { get; set; }
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a message for updating via RTU (Real-Time Update)
	/// </summary>
	[Serializable]
	public class UpdateMessage : BaseMessage
	{
		public UpdateMessage(BaseMessage message = null) : base()
		{
			this.DeviceID = "";
			this.ExcludedDeviceID = "";
			if (message != null)
			{
				this.Type = message.Type;
				this.Data = message.Data;
			}
		}

		/// <summary>
		/// Gets or sets identity of device that received the message
		/// </summary>
		public string DeviceID { get; set; }

		/// <summary>
		/// Gets or sets the identity of excluded devices
		/// </summary>
		public string ExcludedDeviceID { get; set; }
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a message for communicating between services
	/// </summary>
	[Serializable]
	public class CommunicateMessage : BaseMessage
	{
		public CommunicateMessage(string serviceName = null, BaseMessage message = null) : base()
		{
			this.ServiceName = serviceName ?? "";
			if (message != null)
			{
				this.Type = message.Type;
				this.Data = message.Data;
			}
		}

		/// <summary>
		/// Gets or sets name of the service that received and processed the message
		/// </summary>
		public string ServiceName { get; set; }
	}
}