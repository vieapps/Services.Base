namespace net.vieapps.Services
{
	/// <summary>
	/// Presents the state of a service
	/// </summary>
	[System.Serializable]
	public enum ServiceState
	{
		/// <summary>
		/// Initializing state means the service is starting and not ready to register
		/// </summary>
		Initializing,

		/// <summary>
		/// Ready state means the service is initialized and ready to register with router
		/// </summary>
		Ready,

		/// <summary>
		/// Connected state means the service is initialized, registered and connected
		/// </summary>
		Connected,

		/// <summary>
		/// Disconnected state means the service is initialized but disconnected (and un-registered)
		/// </summary>
		Disconnected
	}
}