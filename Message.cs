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
	public class BaseMessage : IServiceMessage
	{
		public BaseMessage() { }

		/// <summary>
		/// Gets or sets type of the message
		/// </summary>
		public string Type { get; set; } = "";

		/// <summary>
		/// Gets or sets data of the message
		/// </summary>
		public JToken Data { get; set; } = new JObject();
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a message for updating via RTU (Real-Time Update)
	/// </summary>
	[Serializable]
	public class UpdateMessage : BaseMessage, IUpdateMessage
	{
		public UpdateMessage(IServiceMessage message = null) : base()
		{
			this.Type = message?.Type ?? "";
			this.Data = message?.Data ?? new JObject();
		}

		/// <summary>
		/// Gets or sets identity of device that received the message
		/// </summary>
		public string DeviceID { get; set; } = "";

		/// <summary>
		/// Gets or sets the identity of excluded devices
		/// </summary>
		public string ExcludedDeviceID { get; set; } = "";
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a message for communicating between services
	/// </summary>
	[Serializable]
	public class CommunicateMessage : BaseMessage, ICommunicateMessage
	{
		public CommunicateMessage(string serviceName = null, IServiceMessage message = null) : base()
		{
			this.ServiceName = serviceName ?? "";
			this.Type = message?.Type ?? "";
			this.Data = message?.Data ?? new JObject();
		}

		/// <summary>
		/// Gets or sets name of the service that received and processed the message
		/// </summary>
		public string ServiceName { get; set; }
	}
}