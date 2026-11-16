<Query Kind="Statements">
  <Namespace>Windows.Devices.Enumeration</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <IncludeWinSDK>true</IncludeWinSDK>
</Query>

(await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture))
	.Select(d => new {d.Name, d.Id})
	.Dump("Video Capture Devices");