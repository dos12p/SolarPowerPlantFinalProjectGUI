using System.IO.Ports;
using System.Text;

namespace FinalProjektGUI;

public partial class MainPage : ContentPage
{
	private SerialPort? _serialPort;
	private StringBuilder _receiveBuffer = new StringBuilder();
	private const int MaxLogLines = 100;
	private List<string> _logLines = new List<string>();

	public MainPage()
	{
		InitializeComponent();
		LoadAvailablePorts();
	}

	private void LoadAvailablePorts()
	{
		try
		{
			string[] ports = SerialPort.GetPortNames();
			ComPortPicker.ItemsSource = ports.ToList();
			if (ports.Length > 0)
			{
				ComPortPicker.SelectedIndex = 0;
			}
		}
		catch (Exception ex)
		{
			DisplayAlert("Error", $"Failed to load COM ports: {ex.Message}", "OK");
		}
	}

	private void OnConnectClicked(object? sender, EventArgs e)
	{
		if (ComPortPicker.SelectedItem == null)
		{
			DisplayAlert("Error", "Please select a COM port", "OK");
			return;
		}

		try
		{
			string portName = ComPortPicker.SelectedItem.ToString()!;
			_serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
			_serialPort.DataReceived += SerialPort_DataReceived;
			_serialPort.Open();

			ConnectButton.IsEnabled = false;
			DisconnectButton.IsEnabled = true;
			ComPortPicker.IsEnabled = false;

			AddToLog($"Connected to {portName}");
		}
		catch (Exception ex)
		{
			DisplayAlert("Error", $"Failed to connect: {ex.Message}", "OK");
		}
	}

	private void OnDisconnectClicked(object? sender, EventArgs e)
	{
		DisconnectSerial();
	}

	private void DisconnectSerial()
	{
		try
		{
			if (_serialPort != null && _serialPort.IsOpen)
			{
				_serialPort.DataReceived -= SerialPort_DataReceived;
				_serialPort.Close();
				_serialPort.Dispose();
				_serialPort = null;
			}

			ConnectButton.IsEnabled = true;
			DisconnectButton.IsEnabled = false;
			ComPortPicker.IsEnabled = true;

			AddToLog("Disconnected");
		}
		catch (Exception ex)
		{
			DisplayAlert("Error", $"Failed to disconnect: {ex.Message}", "OK");
		}
	}

	private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
	{
		try
		{
			SerialPort sp = (SerialPort)sender;
			string data = sp.ReadExisting();
			_receiveBuffer.Append(data);

			// Process complete packets
			string bufferContent = _receiveBuffer.ToString();
			int startIndex = bufferContent.IndexOf("###");
			
			while (startIndex != -1)
			{
				// Look for \r\n after the start marker
				int endIndex = bufferContent.IndexOf("\r\n", startIndex);
				
				if (endIndex != -1)
				{
					// Extract complete packet
					string packet = bufferContent.Substring(startIndex, endIndex - startIndex + 2);
					ProcessPacket(packet);

					// Remove processed packet from buffer
					_receiveBuffer.Remove(0, endIndex + 2);
					bufferContent = _receiveBuffer.ToString();
					startIndex = bufferContent.IndexOf("###");
				}
				else
				{
					// Incomplete packet, wait for more data
					break;
				}
			}

			// Clear buffer if it gets too large (corrupted data)
			if (_receiveBuffer.Length > 1000)
			{
				_receiveBuffer.Clear();
			}
		}
		catch (Exception ex)
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				AddToLog($"Error receiving data: {ex.Message}");
			});
		}
	}

	private void ProcessPacket(string packet)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			try
			{
				AddToLog($"<< IN: {packet.Trim()}");

				// Validate packet format and length
				// Expected format: ###NNNaaaabbbbccccddddeeeeffffggggCCC\r\n
				// Length: 3 + 3 + (6*4) + 4 + 3 + 2 = 39 characters
				if (packet.Length < 37) // Minimum without \r\n
				{
					AddToLog("ERROR: Packet too short");
					return;
				}

				// Parse packet components
				int index = 3; // Skip "###"

				string packetNumber = packet.Substring(index, 3);
				index += 3;

				string adc0 = packet.Substring(index, 4);
				index += 4;

				string adc1 = packet.Substring(index, 4);
				index += 4;

				string adc2 = packet.Substring(index, 4);
				index += 4;

				string adc3 = packet.Substring(index, 4);
				index += 4;

				string adc4 = packet.Substring(index, 4);
				index += 4;

				string adc5 = packet.Substring(index, 4);
				index += 4;

				string digitalInputs = packet.Substring(index, 4);
				index += 4;

				string receivedChecksum = packet.Substring(index, 3);

				// Calculate checksum (sum of all numeric ASCII characters % 1000)
				int calculatedChecksum = 0;
				for (int i = 3; i < index; i++) // Start after "###"
				{
					if (char.IsDigit(packet[i]))
					{
						calculatedChecksum += (int)packet[i];
					}
				}
				calculatedChecksum %= 1000;

				// Update UI
				PacketNumberLabel.Text = packetNumber;
				Adc0Label.Text = $"{adc0} mV";
				Adc1Label.Text = $"{adc1} mV";
				Adc2Label.Text = $"{adc2} mV";
				Adc3Label.Text = $"{adc3} mV";
				Adc4Label.Text = $"{adc4} mV";
				Adc5Label.Text = $"{adc5} mV";

				Di0Label.Text = digitalInputs[0].ToString();
				Di1Label.Text = digitalInputs[1].ToString();
				Di2Label.Text = digitalInputs[2].ToString();
				Di3Label.Text = digitalInputs[3].ToString();

				ReceivedChecksumLabel.Text = receivedChecksum;
				CalculatedChecksumLabel.Text = calculatedChecksum.ToString("D3");

				// Validate checksum
				if (receivedChecksum != calculatedChecksum.ToString("D3"))
				{
					CalculatedChecksumLabel.TextColor = Colors.Red;
					ReceivedChecksumLabel.TextColor = Colors.Red;
				}
				else
				{
					CalculatedChecksumLabel.TextColor = Colors.Green;
					ReceivedChecksumLabel.TextColor = Colors.Green;
				}
			}
			catch (Exception ex)
			{
				AddToLog($"ERROR parsing packet: {ex.Message}");
			}
		});
	}

	private void AddToLog(string message)
	{
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			_logLines.Add($"[{timestamp}] {message}");

			// Keep only the last MaxLogLines
			if (_logLines.Count > MaxLogLines)
			{
				_logLines.RemoveAt(0);
			}

			RawDataLog.Text = string.Join("\n", _logLines);

			// Auto-scroll to bottom if enabled
			if (AutoScrollSwitch.IsToggled)
			{
				await LogScrollView.ScrollToAsync(0, RawDataLog.Height, false);
			}
		});
	}

	private void OnLedToggled(object? sender, ToggledEventArgs e)
	{
		SendLedPacket();
	}

	private void SendLedPacket()
	{
		try
		{
			if (_serialPort == null || !_serialPort.IsOpen)
			{
				return;
			}

			// Build LED state string (4 binary digits) - Active Low
			string led1 = Led1Switch.IsToggled ? "0" : "1";
			string led2 = Led2Switch.IsToggled ? "0" : "1";
			string led3 = Led3Switch.IsToggled ? "0" : "1";
			string led4 = Led4Switch.IsToggled ? "0" : "1";
			string ledStates = led1 + led2 + led3 + led4;

			// Calculate checksum (sum of ASCII values of all numeric characters % 1000)
			int checksum = 0;
			foreach (char c in ledStates)
			{
				checksum += (int)c;
			}
			checksum %= 1000;

			// Build packet: ###xxxx + checksum + \r\n
			string packet = $"###{ledStates}{checksum:D3}\r\n";

			// Send packet
			_serialPort.Write(packet);
			AddToLog($">> OUT: {packet.Trim()}");
		}
		catch (Exception ex)
		{
			AddToLog($"ERROR sending packet: {ex.Message}");
		}
	}

	private void OnClearLogClicked(object? sender, EventArgs e)
	{
		_logLines.Clear();
		RawDataLog.Text = string.Empty;
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		DisconnectSerial();
	}
}
