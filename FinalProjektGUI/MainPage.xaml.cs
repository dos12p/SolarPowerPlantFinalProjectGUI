using System.IO.Ports;
using System.Text;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;
using System.Collections.ObjectModel;

namespace FinalProjektGUI;

public partial class MainPage : ContentPage
{
	private SerialPort? _serialPort;
	private StringBuilder _receiveBuffer = new StringBuilder();
	private const int MaxLogLines = 100;
	private List<string> _logLines = new List<string>();

	// ADC Rolling Average (last 2 values)
	private Queue<int>[] _adcHistory = new Queue<int>[6];
	private const int AverageWindow = 2;

	// Circuit Protection
	private bool _isTripped = false;
	private double _tripThreshold = 1.5; // mA (overcurrent limit) - now configurable
	private bool _isBreakerEnabled = true; // Breaker bypass toggle
	private double _lowVoltageThreshold = 1.9; // V (dead voltage) - now configurable
	private const double RecoveryVoltageThreshold = 2.2; // V
	private bool _isBatteryDead = false;

	// Battery Voltage Averaging
	private Queue<double> _batteryVoltageHistory = new Queue<double>();
	private const int BatteryVoltageAverageWindow = 6;

	// Packet Statistics
	private int _totalPacketsReceived = 0;
	private int _validPackets = 0;
	private int _invalidPackets = 0;
	private int _lastPacketNumber = -1;

	// Traffic Light Mode
	private bool _isTrafficMode = false;
	private System.Timers.Timer? _trafficTimer;
	private int _trafficState = 0; // 0=Green, 1=Yellow, 2=Red
	private readonly int[] _trafficDurations = { 4000, 2000, 7000 }; // Green, Yellow, Red in ms

	// Christmas Mode
	private bool _isChristmasMode = false;
	private System.Timers.Timer? _christmasTimer;
	private int _christmasState = 0;
	private readonly int[] _christmasDurations = { 500, 500, 500, 500, 500, 500 }; // Pattern timing in ms

	// Graph Data Collections
	private ObservableCollection<ObservablePoint> _solarData = new();
	private ObservableCollection<ObservablePoint> _batteryData = new();
	private ObservableCollection<ObservablePoint> _batteryCurrentData = new(); // New: Battery current (+ charge, - discharge)
	private ObservableCollection<ObservablePoint> _totalLoadData = new();
	private ObservableCollection<ObservablePoint> _yellowData = new();
	private ObservableCollection<ObservablePoint> _redData = new();
	private ObservableCollection<ObservablePoint> _greenData = new();
	private int _timeWindowSeconds = 10; // Default 10 seconds

	// Individual scale for each current chart
	private int _totalLoadScaleMax = 4;
	private int _yellowScaleMax = 4;
	private int _redScaleMax = 4;
	private int _greenScaleMax = 4;
	private int _combinedCurrentScaleMax = 4;

	// Keep track of start time for X-axis
	private DateTime _graphStartTime = DateTime.Now;
	private List<Axis> _allXAxes = new();

	// Y-axis references for individual charts
	private Axis? _totalLoadYAxis;
	private Axis? _yellowYAxis;
	private Axis? _redYAxis;
	private Axis? _greenYAxis;
	private Axis? _batteryCurrentYAxis; // New: Battery current Y-axis
	private Axis? _combinedCurrentYAxis;

	// Battery current scale (will be ± this value)
	private int _batteryCurrentScaleMax = 4;

	public MainPage()
	{
		InitializeComponent();
		LoadAvailablePorts();
		
		// Initialize ADC history queues
		for (int i = 0; i < 6; i++)
		{
			_adcHistory[i] = new Queue<int>();
		}

		// Initialize graphs
		InitializeGraphs();
		LedModePicker.SelectedIndex = 0; // Default to Manual
		TimeWindowPicker.SelectedIndex = 0; // Default to 10 seconds
		TotalLoadScalePicker.SelectedIndex = 3; // Default to 4mA
		YellowScalePicker.SelectedIndex = 3;
		RedScalePicker.SelectedIndex = 3;
		GreenScalePicker.SelectedIndex = 3;
		BatteryCurrentScalePicker.SelectedIndex = 3; // Default to 4mA
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

			// Initialize LEDs off
			Led2Switch.IsToggled = false;
			Led3Switch.IsToggled = false;
			Led4Switch.IsToggled = false;
			SendLedPacket();

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
				// Expected format: ###NNN AAAA BBBB CCCC DDDD EEEE FFFF GGGG CCC\r\n
				// where spaces are included between values
				if (packet.Length < 45) // Minimum without \r\n (3 + 3 + 1 + 4 + 1 + 4 + 1 + 4 + 1 + 4 + 1 + 4 + 1 + 4 + 1 + 4 + 1 + 3 = 45)
				{
					AddToLog("ERROR: Packet too short");
					return;
				}

				// Parse packet components with spaces
				int index = 3; // Skip "###"

				string packetNumber = packet.Substring(index, 3);
				index += 4; // 3 digits + 1 space

				string adc0 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

				string adc1 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

				string adc2 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

				string adc3 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

				string adc4 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

				string adc5 = packet.Substring(index, 4);
				index += 5; // 4 digits + 1 space

			string digitalInputs = packet.Substring(index, 4);
			index += 5; // 4 digits + 1 space

			string receivedChecksum = packet.Substring(index, 3);

			// Calculate checksum: sum of ASCII values from start to just before checksum
			// Matches Meadow protocol: ComputeChecksum sums all bytes from "###" through digital inputs
			string rawForChecksum = packet.Substring(0, index - 1); // Everything before the space before checksum
			int calculatedChecksum = 0;
			byte[] bytes = Encoding.ASCII.GetBytes(rawForChecksum);
			foreach (byte b in bytes)
				calculatedChecksum += b;
			calculatedChecksum %= 1000;

			// Update packet statistics
			_totalPacketsReceived++;
			
			// Check for packet loss (gaps in packet numbers)
			if (int.TryParse(packetNumber, out int currentPacketNum))
			{
				if (_lastPacketNumber != -1)
				{
					int expectedPacketNum = (_lastPacketNumber + 1) % 1000; // Packet numbers wrap at 1000
					if (currentPacketNum != expectedPacketNum)
					{
						// Calculate packets lost
						int packetsLost = (currentPacketNum - expectedPacketNum + 1000) % 1000;
						_invalidPackets += packetsLost;
					}
				}
				_lastPacketNumber = currentPacketNum;
			}

			// Validate checksum
			bool checksumValid = receivedChecksum == calculatedChecksum.ToString("D3");
			if (checksumValid)
			{
				_validPackets++;
			}
			else
			{
				_invalidPackets++;
			}

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

			// Update checksum display colors
			if (checksumValid)
			{
				CalculatedChecksumLabel.TextColor = Colors.Green;
				ReceivedChecksumLabel.TextColor = Colors.Green;
			}
			else
			{
				CalculatedChecksumLabel.TextColor = Colors.Red;
				ReceivedChecksumLabel.TextColor = Colors.Red;
			}

			// Update packet statistics
			PacketLossLabel.Text = _invalidPackets.ToString();
			double accuracy = _totalPacketsReceived > 0 ? (_validPackets * 100.0 / _totalPacketsReceived) : 100.0;
			PacketAccuracyLabel.Text = $"{accuracy:F1}%";
			PacketAccuracyLabel.TextColor = accuracy >= 95 ? Colors.Green : (accuracy >= 80 ? Colors.Orange : Colors.Red);			// Calculate derived values
			if (int.TryParse(adc0, out int adc0Val) &&
			    int.TryParse(adc1, out int adc1Val) &&
			    int.TryParse(adc2, out int adc2Val) &&
			    int.TryParse(adc3, out int adc3Val) &&
			    int.TryParse(adc4, out int adc4Val) &&
			    int.TryParse(adc5, out int adc5Val))
			{
				// Update ADC history and calculate averages
				int[] currentValues = { adc0Val, adc1Val, adc2Val, adc3Val, adc4Val, adc5Val };
				double[] avgValues = new double[6];

				for (int i = 0; i < 6; i++)
				{
					_adcHistory[i].Enqueue(currentValues[i]);
					if (_adcHistory[i].Count > AverageWindow)
					{
						_adcHistory[i].Dequeue();
					}
					avgValues[i] = _adcHistory[i].Average();
				}

				// Solar Output (ADC0 in volts)
				double solarVoltage = avgValues[0] / 1000.0;
				SolarOutputLabel.Text = $"{solarVoltage:F3} V";

				// Battery Voltage (ADC4 in volts) - with extended averaging
				double instantVoltage = avgValues[4] / 1000.0;
				_batteryVoltageHistory.Enqueue(instantVoltage);
				if (_batteryVoltageHistory.Count > BatteryVoltageAverageWindow)
				{
					_batteryVoltageHistory.Dequeue();
				}
				double batteryVoltage = _batteryVoltageHistory.Average();
				BatteryVoltageLabel.Text = $"{batteryVoltage:F3} V";

				// Update battery bar (1.8V to 2.7V)
				double batteryPercentage = Math.Clamp((batteryVoltage - 1.8) / (2.7 - 1.8), 0.0, 1.0);
				BatteryBar.Progress = batteryPercentage;

				// Update battery bar color based on voltage
				if (batteryVoltage < 1.9)
				{
					BatteryBar.ProgressColor = Colors.Red;
				}
			else
			{
				BatteryBar.ProgressColor = Colors.Green;
			}

			// Check battery dead state
			if (batteryVoltage < _lowVoltageThreshold)
			{
				_isBatteryDead = true;
			}
			else if (batteryVoltage >= RecoveryVoltageThreshold)
			{
				_isBatteryDead = false;
			}

			// Battery Status (ADC5 - ADC4) / 100
			double batteryCurrent = (avgValues[5] - avgValues[4]) / 100.0;				if (_isBatteryDead)
				{
					BatteryStatusLabel.Text = "DEAD";
					BatteryStatusLabel.TextColor = Colors.Red;
				}
				else if (batteryCurrent >= 0)
				{
					BatteryStatusLabel.Text = $"CHARGING at {Math.Abs(batteryCurrent):F1} mA";
					BatteryStatusLabel.TextColor = Colors.Green;
				}
				else
				{
					BatteryStatusLabel.Text = $"DISCHARGING at {Math.Abs(batteryCurrent):F1} mA";
					BatteryStatusLabel.TextColor = Colors.OrangeRed;
				}

				// LED Currents (ADC5 - LED_ADC) / 220
				double yellowCurrent = (avgValues[5] - avgValues[1]) / 220.0;
				double redCurrent = (avgValues[5] - avgValues[2]) / 220.0;
				double greenCurrent = (avgValues[5] - avgValues[3]) / 220.0;

				YellowCurrentLabel.Text = $"{yellowCurrent:F1} mA";
				RedCurrentLabel.Text = $"{redCurrent:F1} mA";
				GreenCurrentLabel.Text = $"{greenCurrent:F1} mA";

				// Total Load Current
				double totalLoad = yellowCurrent + redCurrent + greenCurrent;
				TotalLoadCurrentLabel.Text = $"{totalLoad:F1} mA";

			// Add data to graphs (raw values, no averaging)
			AddGraphData(solarVoltage, batteryVoltage, batteryCurrent, totalLoad, yellowCurrent, redCurrent, greenCurrent);

			// Check for trip conditions (latching) - only if breaker is enabled
			if (!_isTripped && _isBreakerEnabled)
			{
				// Low voltage trip
				if (batteryVoltage < _lowVoltageThreshold)
				{
					_isTripped = true;
					TripCircuit();
					AddToLog($"TRIP: Low battery voltage ({batteryVoltage:F3} V < {_lowVoltageThreshold} V)");
				}
				// Discharge overcurrent trip
				else if (batteryCurrent < 0 && Math.Abs(batteryCurrent) > _tripThreshold)
				{
					_isTripped = true;
					TripCircuit();
					AddToLog($"TRIP: Battery discharge overcurrent ({Math.Abs(batteryCurrent):F1} mA > {_tripThreshold} mA)");
				}
			}
			}
		}
		catch (Exception ex)
		{
			AddToLog($"ERROR parsing packet: {ex.Message}");
		}
		});
	}	private void AddToLog(string message)
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
		if (!_isTrafficMode)
		{
			SendLedPacket();
		}
	}

	private void SendLedPacket(bool forceBlueOn = false)
	{
		try
		{
			if (_serialPort == null || !_serialPort.IsOpen)
			{
				return;
			}

			// Build LED state string (4 binary digits) - Active Low for LEDs, Active High for Blue LED
			// First digit: Blue LED (1 = ON when tripped or forced, 0 = OFF normally)
			// Remaining digits: Yellow, Red, Green LEDs (0 = ON, 1 = OFF)
			string led1 = (_isTripped || forceBlueOn) ? "1" : "0"; // Blue LED - Active High
			string led2 = (!_isTripped && Led2Switch.IsToggled) ? "0" : "1"; // Yellow - Active Low
			string led3 = (!_isTripped && Led3Switch.IsToggled) ? "0" : "1"; // Red - Active Low
			string led4 = (!_isTripped && Led4Switch.IsToggled) ? "0" : "1"; // Green - Active Low
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

	private void TripCircuit()
	{
		// Don't trip if breaker is bypassed
		if (!_isBreakerEnabled)
			return;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Switch to manual mode
			if (_isTrafficMode || _isChristmasMode)
			{
				_isTrafficMode = false;
				_isChristmasMode = false;
				LedModePicker.SelectedIndex = 0; // Set to Manual
			}

			// Stop traffic timer if running
			if (_trafficTimer != null)
			{
				_trafficTimer.Stop();
				_trafficTimer.Dispose();
				_trafficTimer = null;
			}

			// Stop Christmas timer if running
			if (_christmasTimer != null)
			{
				_christmasTimer.Stop();
				_christmasTimer.Dispose();
				_christmasTimer = null;
			}

			// Turn off all LED switches
			Led2Switch.IsToggled = false;
			Led3Switch.IsToggled = false;
			Led4Switch.IsToggled = false;

			// Disable LED switches
			Led2Switch.IsEnabled = false;
			Led3Switch.IsEnabled = false;
			Led4Switch.IsEnabled = false;

			// Update circuit status display
			CircuitStatusLabel.Text = "TRIP";
			CircuitStatusLabel.TextColor = Colors.Red;

			// Send packet to turn on Blue LED and turn off all others
			SendLedPacket();
		});
	}

	private void OnResetBreakerClicked(object? sender, EventArgs e)
	{
		// Don't do anything if breaker is bypassed
		if (!_isBreakerEnabled)
		{
			AddToLog("Reset breaker ignored - breaker is bypassed");
			return;
		}

		// Check if battery is dead
		if (_isBatteryDead)
		{
			// Immediately trip again
			_isTripped = true;
			TripCircuit();
			AddToLog("RESET FAILED: Charge battery to reset breaker (battery voltage must exceed 2.2V)");
			DisplayAlert("Reset Failed", "Battery is dead. Charge battery above 2.2V to reset breaker.", "OK");
			return;
		}

		_isTripped = false;

		// Switch to manual mode and turn off all LEDs
		_isTrafficMode = false;
		_isChristmasMode = false;
		LedModePicker.SelectedIndex = 0; // Set to Manual

		// Stop any running timers
		if (_trafficTimer != null)
		{
			_trafficTimer.Stop();
			_trafficTimer.Dispose();
			_trafficTimer = null;
		}
		if (_christmasTimer != null)
		{
			_christmasTimer.Stop();
			_christmasTimer.Dispose();
			_christmasTimer = null;
		}

		// Turn off all LEDs except blue (LED1)
		Led2Switch.IsToggled = false; // Yellow
		Led3Switch.IsToggled = false; // Red
		Led4Switch.IsToggled = false; // Green

		// Re-enable LED switches
		Led2Switch.IsEnabled = true;
		Led3Switch.IsEnabled = true;
		Led4Switch.IsEnabled = true;

		// Update circuit status display
		CircuitStatusLabel.Text = "ON";
		CircuitStatusLabel.TextColor = Colors.Green;

		// Send packet to update LED states
		SendLedPacket();

		AddToLog("Circuit breaker RESET - Mode set to Manual, all LEDs turned off");
	}

	private void OnLedModeChanged(object? sender, EventArgs e)
	{
		if (LedModePicker.SelectedIndex == -1)
			return;

		string selectedMode = LedModePicker.SelectedItem.ToString() ?? "Manual";

		// Stop all timers first
		if (_trafficTimer != null)
		{
			_trafficTimer.Stop();
			_trafficTimer.Dispose();
			_trafficTimer = null;
		}
		if (_christmasTimer != null)
		{
			_christmasTimer.Stop();
			_christmasTimer.Dispose();
			_christmasTimer = null;
		}

		// Reset mode flags
		_isTrafficMode = false;
		_isChristmasMode = false;

		switch (selectedMode)
		{
			case "Manual":
				// Turn off all LEDs
				Led2Switch.IsToggled = false;
				Led3Switch.IsToggled = false;
				Led4Switch.IsToggled = false;
				SendLedPacket();

				// Re-enable manual LED switches if not tripped
				if (!_isTripped)
				{
					Led2Switch.IsEnabled = true;
					Led3Switch.IsEnabled = true;
					Led4Switch.IsEnabled = true;
				}
				AddToLog("Manual mode ENABLED");
				break;

			case "Traffic":
				_isTrafficMode = true;
				// Disable manual LED switches
				Led2Switch.IsEnabled = false;
				Led3Switch.IsEnabled = false;
				Led4Switch.IsEnabled = false;
				// Start traffic light cycle
				_trafficState = 0;
				StartTrafficCycle();
				AddToLog("Traffic mode ENABLED");
				break;

			case "Christmas":
				_isChristmasMode = true;
				// Disable manual LED switches
				Led2Switch.IsEnabled = false;
				Led3Switch.IsEnabled = false;
				Led4Switch.IsEnabled = false;
				// Start Christmas pattern
				_christmasState = 0;
				StartChristmasCycle();
				AddToLog("Christmas mode ENABLED");
				break;
		}
	}

	private void StartTrafficCycle()
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Set initial state (Green)
			UpdateTrafficLights();

			// Create and start timer
			_trafficTimer = new System.Timers.Timer(_trafficDurations[_trafficState]);
			_trafficTimer.Elapsed += OnTrafficTimerElapsed;
			_trafficTimer.AutoReset = false;
			_trafficTimer.Start();
		});
	}

	private void OnTrafficTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
	{
		if (!_isTrafficMode || _isTripped)
		{
			return;
		}

		// Move to next state
		_trafficState = (_trafficState + 1) % 3;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			UpdateTrafficLights();

			// Restart timer with new duration
			if (_trafficTimer != null)
			{
				_trafficTimer.Interval = _trafficDurations[_trafficState];
				_trafficTimer.Start();
			}
		});
	}

	private void UpdateTrafficLights()
	{
		// Turn off all LEDs first
		Led2Switch.IsToggled = false; // Yellow
		Led3Switch.IsToggled = false; // Red
		Led4Switch.IsToggled = false; // Green

		// Turn on current state LED
		switch (_trafficState)
		{
			case 0: // Green
				Led4Switch.IsToggled = true;
				break;
			case 1: // Yellow
				Led2Switch.IsToggled = true;
				break;
			case 2: // Red
				Led3Switch.IsToggled = true;
				break;
		}

		SendLedPacket();
	}

	// Christmas mode methods
	private void StartChristmasCycle()
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Set initial state
			UpdateChristmasLights();

			// Create and start timer
			_christmasTimer = new System.Timers.Timer(_christmasDurations[_christmasState]);
			_christmasTimer.Elapsed += OnChristmasTimerElapsed;
			_christmasTimer.AutoReset = false;
			_christmasTimer.Start();
		});
	}

	private void OnChristmasTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
	{
		if (!_isChristmasMode || _isTripped)
		{
			return;
		}

		// Move to next state
		_christmasState = (_christmasState + 1) % 6;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			UpdateChristmasLights();

			// Restart timer with new duration
			if (_christmasTimer != null)
			{
				_christmasTimer.Interval = _christmasDurations[_christmasState];
				_christmasTimer.Start();
			}
		});
	}

	private void UpdateChristmasLights()
	{
		// Christmas pattern: alternating red/green with blue flashing
		// Pattern states: 0=Red+Blue, 1=Red, 2=Green+Blue, 3=Green, 4=All+Blue, 5=Off
		switch (_christmasState)
		{
			case 0: // Red + Blue flash
				Led3Switch.IsToggled = true;  // Red on (active-low)
				Led2Switch.IsToggled = false; // Yellow off
				Led4Switch.IsToggled = false; // Green off
				break;
			case 1: // Red only
				Led3Switch.IsToggled = true;  // Red on
				Led2Switch.IsToggled = false; // Yellow off
				Led4Switch.IsToggled = false; // Green off
				break;
			case 2: // Green + Blue flash
				Led3Switch.IsToggled = false; // Red off
				Led2Switch.IsToggled = false; // Yellow off
				Led4Switch.IsToggled = true;  // Green on (active-low)
				break;
			case 3: // Green only
				Led3Switch.IsToggled = false; // Red off
				Led2Switch.IsToggled = false; // Yellow off
				Led4Switch.IsToggled = true;  // Green on
				break;
			case 4: // All + Blue flash
				Led3Switch.IsToggled = true;  // Red on
				Led2Switch.IsToggled = true;  // Yellow on
				Led4Switch.IsToggled = true;  // Green on
				break;
			case 5: // All off
				Led3Switch.IsToggled = false;
				Led2Switch.IsToggled = false;
				Led4Switch.IsToggled = false;
				break;
		}
		
		// Send packet with Blue LED active on states 0, 2, 4
		SendLedPacket(_christmasState == 0 || _christmasState == 2 || _christmasState == 4);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		
		if (_trafficTimer != null)
		{
			_trafficTimer.Stop();
			_trafficTimer.Dispose();
			_trafficTimer = null;
		}
		
		if (_christmasTimer != null)
		{
			_christmasTimer.Stop();
			_christmasTimer.Dispose();
			_christmasTimer = null;
		}
		
		DisconnectSerial();
	}

	// Graph Methods
	private void InitializeGraphs()
	{
		// Increased margins: left, top, right, bottom to keep labels outside gridlines
		var drawMargin = new LiveChartsCore.Measure.Margin(70, 50, 30, 50);
		_allXAxes.Clear();

		// Combined Voltage Chart
		CombinedVoltageChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _solarData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Orange) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Solar"
			},
			new LineSeries<ObservablePoint>
			{
				Values = _batteryData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Battery"
			}
		};
		var combinedVoltageXAxis = CreateTimeAxis();
		_allXAxes.Add(combinedVoltageXAxis);
		CombinedVoltageChart.XAxes = new[] { combinedVoltageXAxis };
		CombinedVoltageChart.YAxes = new[] { CreateVoltageAxis() };
		CombinedVoltageChart.DrawMargin = drawMargin;
		CombinedVoltageChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Top;
		CombinedVoltageChart.LegendTextPaint = new SolidColorPaint(SKColors.White);

		// Combined Current Chart
		CombinedCurrentChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _totalLoadData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.DeepSkyBlue) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Total Load"
			},
			new LineSeries<ObservablePoint>
			{
				Values = _yellowData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Gold) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Yellow LED"
			},
			new LineSeries<ObservablePoint>
			{
				Values = _redData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Red LED"
			},
			new LineSeries<ObservablePoint>
			{
				Values = _greenData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 2 },
				GeometrySize = 0,
				LineSmoothness = 0,
				Name = "Green LED"
			}
		};
		var combinedCurrentXAxis = CreateTimeAxis();
		_allXAxes.Add(combinedCurrentXAxis);
		_combinedCurrentYAxis = CreateCurrentAxis(_combinedCurrentScaleMax);
		CombinedCurrentChart.XAxes = new[] { combinedCurrentXAxis };
		CombinedCurrentChart.YAxes = new[] { _combinedCurrentYAxis };
		CombinedCurrentChart.DrawMargin = drawMargin;
		CombinedCurrentChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Top;
		CombinedCurrentChart.LegendTextPaint = new SolidColorPaint(SKColors.White);

		// Solar Chart
		SolarChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _solarData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Orange) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var solarXAxis = CreateTimeAxis();
		_allXAxes.Add(solarXAxis);
		SolarChart.XAxes = new[] { solarXAxis };
		SolarChart.YAxes = new[] { CreateVoltageAxis() };
		SolarChart.DrawMargin = drawMargin;

		// Battery Chart
		BatteryChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _batteryData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var batteryXAxis = CreateTimeAxis();
		_allXAxes.Add(batteryXAxis);
		BatteryChart.XAxes = new[] { batteryXAxis };
		BatteryChart.YAxes = new[] { CreateVoltageAxis() };
		BatteryChart.DrawMargin = drawMargin;

		// Total Load Chart
		TotalLoadChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _totalLoadData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.DeepSkyBlue) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var totalLoadXAxis = CreateTimeAxis();
		_allXAxes.Add(totalLoadXAxis);
		_totalLoadYAxis = CreateCurrentAxis(_totalLoadScaleMax);
		TotalLoadChart.XAxes = new[] { totalLoadXAxis };
		TotalLoadChart.YAxes = new[] { _totalLoadYAxis };
		TotalLoadChart.DrawMargin = drawMargin;

		// Yellow LED Chart
		YellowChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _yellowData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Gold) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var yellowXAxis = CreateTimeAxis();
		_allXAxes.Add(yellowXAxis);
		_yellowYAxis = CreateCurrentAxis(_yellowScaleMax);
		YellowChart.XAxes = new[] { yellowXAxis };
		YellowChart.YAxes = new[] { _yellowYAxis };
		YellowChart.DrawMargin = drawMargin;

		// Red LED Chart
		RedChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _redData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var redXAxis = CreateTimeAxis();
		_allXAxes.Add(redXAxis);
		_redYAxis = CreateCurrentAxis(_redScaleMax);
		RedChart.XAxes = new[] { redXAxis };
		RedChart.YAxes = new[] { _redYAxis };
		RedChart.DrawMargin = drawMargin;

		// Green LED Chart
		GreenChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _greenData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.LimeGreen) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var greenXAxis = CreateTimeAxis();
		_allXAxes.Add(greenXAxis);
		_greenYAxis = CreateCurrentAxis(_greenScaleMax);
		GreenChart.XAxes = new[] { greenXAxis };
		GreenChart.YAxes = new[] { _greenYAxis };
		GreenChart.DrawMargin = drawMargin;

		// Battery Current Chart (Charge/Discharge)
		BatteryCurrentChart.Series = new ISeries[]
		{
			new LineSeries<ObservablePoint>
			{
				Values = _batteryCurrentData,
				Fill = null,
				Stroke = new SolidColorPaint(SKColors.Cyan) { StrokeThickness = 3 },
				GeometrySize = 0,
				LineSmoothness = 0
			}
		};
		var batteryCurrentXAxis = CreateTimeAxis();
		_allXAxes.Add(batteryCurrentXAxis);
		_batteryCurrentYAxis = CreateBatteryCurrentAxis(_batteryCurrentScaleMax);
		BatteryCurrentChart.XAxes = new[] { batteryCurrentXAxis };
		BatteryCurrentChart.YAxes = new[] { _batteryCurrentYAxis };
		BatteryCurrentChart.DrawMargin = drawMargin;
	}

	private Axis CreateTimeAxis()
	{
		// Calculate a nice step size based on time window
		double stepSize = _timeWindowSeconds switch
		{
			<= 10 => 2,      // 2 second intervals for 10s window
			<= 30 => 5,      // 5 second intervals for 30s window  
			<= 60 => 10,     // 10 second intervals for 1min window
			<= 120 => 20,    // 20 second intervals for 2min window
			<= 300 => 60,    // 60 second intervals for 5min window
			_ => 120         // 120 second intervals for 10min window
		};

		return new Axis
		{
			Labeler = value => TimeSpan.FromSeconds(value).ToString(@"mm\:ss"),
			MinStep = stepSize,
			ForceStepToMin = true,
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			LabelsRotation = 0,
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			AnimationsSpeed = TimeSpan.FromMilliseconds(0),
			IsVisible = true,
			ShowSeparatorLines = true
		};
	}

	private Axis CreateVoltageAxis()
	{
		return new Axis
		{
			MinLimit = 0,
			MaxLimit = 3.5,
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			ShowSeparatorLines = true
		};
	}

	private Axis CreateCurrentAxis(int maxScale)
	{
		return new Axis
		{
			MinLimit = 0,
			MaxLimit = maxScale,
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			AnimationsSpeed = TimeSpan.FromMilliseconds(0),
			ShowSeparatorLines = true
		};
	}

	private Axis CreateBatteryCurrentAxis(int maxScale)
	{
		// Battery current axis shows negative (discharge) and positive (charge)
		return new Axis
		{
			MinLimit = -maxScale,
			MaxLimit = maxScale,
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			AnimationsSpeed = TimeSpan.FromMilliseconds(0),
			ShowSeparatorLines = true
		};
	}

	private void AddGraphData(double solar, double battery, double batteryCurrent, double totalLoad, double yellow, double red, double green)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			try
			{
				var now = DateTime.Now;
				double secondsElapsed = (now - _graphStartTime).TotalSeconds;

				// Add data points with actual elapsed time as X value
				_solarData.Add(new ObservablePoint(secondsElapsed, solar));
				_batteryData.Add(new ObservablePoint(secondsElapsed, battery));
				_batteryCurrentData.Add(new ObservablePoint(secondsElapsed, batteryCurrent)); // Add battery current (negative = discharge, positive = charge)
				_totalLoadData.Add(new ObservablePoint(secondsElapsed, totalLoad));
				_yellowData.Add(new ObservablePoint(secondsElapsed, yellow));
				_redData.Add(new ObservablePoint(secondsElapsed, red));
				_greenData.Add(new ObservablePoint(secondsElapsed, green));

				// Calculate visible window boundaries
				double minLimit, maxLimit;
				
				// Update X-axis window to show most recent time window
				if (secondsElapsed > _timeWindowSeconds)
				{
					// Calculate step size for current window
					double stepSize = _timeWindowSeconds switch
					{
						<= 10 => 2,
						<= 30 => 5,
						<= 60 => 10,
						<= 120 => 20,
						<= 300 => 60,
						_ => 120
					};

					// Round minLimit to nearest step for visual stability
					double rawMin = secondsElapsed - _timeWindowSeconds;
					minLimit = Math.Floor(rawMin / stepSize) * stepSize;
					maxLimit = minLimit + _timeWindowSeconds;

					foreach (var axis in _allXAxes)
					{
						axis.MinLimit = minLimit;
						axis.MaxLimit = maxLimit;
					}

					
				}
				else
				{
					// Still filling initial window
					minLimit = 0;
					maxLimit = _timeWindowSeconds;
					
					foreach (var axis in _allXAxes)
					{
						axis.MinLimit = minLimit;
						axis.MaxLimit = maxLimit;
					}
				}
				
				// Always keep data for 10 minutes (600 seconds), regardless of visible window
				// Only remove data older than 10 minutes from current time
				double maxDataRetentionSeconds = 600; // 10 minutes
				double dataRemovalCutoff = secondsElapsed - maxDataRetentionSeconds;
				
				if (dataRemovalCutoff > 0)
				{
					RemoveOldData(_solarData, dataRemovalCutoff);
					RemoveOldData(_batteryData, dataRemovalCutoff);
					RemoveOldData(_batteryCurrentData, dataRemovalCutoff); // Add battery current cleanup
					RemoveOldData(_totalLoadData, dataRemovalCutoff);
					RemoveOldData(_yellowData, dataRemovalCutoff);
					RemoveOldData(_redData, dataRemovalCutoff);
					RemoveOldData(_greenData, dataRemovalCutoff);
				}
			}
			catch (Exception ex)
			{
				AddToLog($"ERROR adding graph data: {ex.Message}");
			}
		});
	}

	private void RemoveOldData(ObservableCollection<ObservablePoint> data, double cutoffSeconds)
	{
		while (data.Count > 0 && data[0].X < cutoffSeconds)
		{
			data.RemoveAt(0);
		}
	}

	private void OnViewModeChanged(object? sender, CheckedChangedEventArgs e)
	{
		if (MonitorModeRadio.IsChecked)
		{
			MonitorView.IsVisible = true;
			GraphView.IsVisible = false;
		}
		else if (GraphModeRadio.IsChecked)
		{
			MonitorView.IsVisible = false;
			GraphView.IsVisible = true;
		}
	}

	private void OnBreakerToggled(object? sender, ToggledEventArgs e)
	{
		_isBreakerEnabled = e.Value;
		
		if (_isBreakerEnabled)
		{
			AddToLog("Breaker ENABLED");
			// Update circuit status if not tripped
			if (!_isTripped)
			{
				CircuitStatusLabel.Text = "ON";
				CircuitStatusLabel.TextColor = Colors.Green;
			}
		}
		else
		{
			AddToLog("Breaker BYPASSED");
			CircuitStatusLabel.Text = "BREAKER BYPASSED";
			CircuitStatusLabel.TextColor = Colors.Orange;
			
			// If currently tripped, reset the trip state
			if (_isTripped)
			{
				_isTripped = false;
				Led2Switch.IsEnabled = true;
				Led3Switch.IsEnabled = true;
				Led4Switch.IsEnabled = true;
			}
		}
	}

	private void OnMaxDischargeUnfocused(object? sender, FocusEventArgs e)
	{
		if (sender is not Entry entry)
			return;
			
		if (string.IsNullOrWhiteSpace(entry.Text))
		{
			entry.Text = _tripThreshold.ToString("F1");
			return;
		}

		if (double.TryParse(entry.Text, out double value) && value >= 0.1 && value <= 100)
		{
			_tripThreshold = value;
			entry.Text = value.ToString("F1"); // Format to 1 decimal place
			AddToLog($"Max discharge threshold set to {value:F1} mA");
		}
		else
		{
			// Invalid input, revert to previous value
			entry.Text = _tripThreshold.ToString("F1");
			AddToLog($"Invalid discharge value. Must be between 0.1 and 100 mA.");
		}
	}

	private void OnDeadVoltageUnfocused(object? sender, FocusEventArgs e)
	{
		if (sender is not Entry entry)
			return;
			
		if (string.IsNullOrWhiteSpace(entry.Text))
		{
			entry.Text = _lowVoltageThreshold.ToString("F1");
			return;
		}

		if (double.TryParse(entry.Text, out double value) && value >= 0.1 && value <= 5.0)
		{
			_lowVoltageThreshold = value;
			entry.Text = value.ToString("F1"); // Format to 1 decimal place
			AddToLog($"Dead voltage threshold set to {value:F1} V");
		}
		else
		{
			// Invalid input, revert to previous value
			entry.Text = _lowVoltageThreshold.ToString("F1");
			AddToLog($"Invalid voltage value. Must be between 0.1 and 5.0 V.");
		}
	}

	private void OnTimeWindowChanged(object? sender, EventArgs e)
	{
		if (TimeWindowPicker.SelectedIndex == -1)
			return;

		switch (TimeWindowPicker.SelectedIndex)
		{
			case 0: _timeWindowSeconds = 10; break;
		case 1: _timeWindowSeconds = 30; break;
		case 2: _timeWindowSeconds = 60; break;
		case 3: _timeWindowSeconds = 120; break;
		case 4: _timeWindowSeconds = 300; break;
		case 5: _timeWindowSeconds = 600; break;
		}
		
		// Reinitialize graphs with new time window and step size
		InitializeGraphs();
	}	private void OnClearSolarChart(object? sender, EventArgs e)
	{
		_solarData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = SolarChart.Series;
		SolarChart.Series = Array.Empty<ISeries>();
		SolarChart.Series = currentSeries;
	}

	private void OnClearBatteryChart(object? sender, EventArgs e)
	{
		_batteryData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = BatteryChart.Series;
		BatteryChart.Series = Array.Empty<ISeries>();
		BatteryChart.Series = currentSeries;
	}

	private void OnClearTotalLoadChart(object? sender, EventArgs e)
	{
		_totalLoadData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = TotalLoadChart.Series;
		TotalLoadChart.Series = Array.Empty<ISeries>();
		TotalLoadChart.Series = currentSeries;
	}

	private void OnClearYellowChart(object? sender, EventArgs e)
	{
		_yellowData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = YellowChart.Series;
		YellowChart.Series = Array.Empty<ISeries>();
		YellowChart.Series = currentSeries;
	}

	private void OnClearRedChart(object? sender, EventArgs e)
	{
		_redData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = RedChart.Series;
		RedChart.Series = Array.Empty<ISeries>();
		RedChart.Series = currentSeries;
	}

	private void OnClearGreenChart(object? sender, EventArgs e)
	{
		_greenData.Clear();
		// Force chart repaint to remove cached rendering artifacts
		var currentSeries = GreenChart.Series;
		GreenChart.Series = Array.Empty<ISeries>();
		GreenChart.Series = currentSeries;
	}

	private void OnTotalLoadScaleChanged(object? sender, EventArgs e)
	{
		if (TotalLoadScalePicker.SelectedIndex == -1)
			return;

		_totalLoadScaleMax = TotalLoadScalePicker.SelectedIndex + 1;
		if (_totalLoadYAxis != null)
			_totalLoadYAxis.MaxLimit = _totalLoadScaleMax;
		if (_combinedCurrentYAxis != null)
			_combinedCurrentYAxis.MaxLimit = _combinedCurrentScaleMax;
	}

	private void OnYellowScaleChanged(object? sender, EventArgs e)
	{
		if (YellowScalePicker.SelectedIndex == -1)
			return;

		_yellowScaleMax = YellowScalePicker.SelectedIndex + 1;
		if (_yellowYAxis != null)
			_yellowYAxis.MaxLimit = _yellowScaleMax;
		if (_combinedCurrentYAxis != null)
			_combinedCurrentYAxis.MaxLimit = _combinedCurrentScaleMax;
	}

	private void OnRedScaleChanged(object? sender, EventArgs e)
	{
		if (RedScalePicker.SelectedIndex == -1)
			return;

		_redScaleMax = RedScalePicker.SelectedIndex + 1;
		if (_redYAxis != null)
			_redYAxis.MaxLimit = _redScaleMax;
		if (_combinedCurrentYAxis != null)
			_combinedCurrentYAxis.MaxLimit = _combinedCurrentScaleMax;
	}

	private void OnGreenScaleChanged(object? sender, EventArgs e)
	{
		if (GreenScalePicker.SelectedIndex == -1)
			return;

		_greenScaleMax = GreenScalePicker.SelectedIndex + 1;
		if (_greenYAxis != null)
			_greenYAxis.MaxLimit = _greenScaleMax;
		if (_combinedCurrentYAxis != null)
			_combinedCurrentYAxis.MaxLimit = _combinedCurrentScaleMax;
	}

	private void OnBatteryCurrentScaleChanged(object? sender, EventArgs e)
	{
		if (BatteryCurrentScalePicker.SelectedIndex == -1)
			return;

		_batteryCurrentScaleMax = BatteryCurrentScalePicker.SelectedIndex + 1;
		if (_batteryCurrentYAxis != null)
		{
			_batteryCurrentYAxis.MaxLimit = _batteryCurrentScaleMax;
			_batteryCurrentYAxis.MinLimit = -_batteryCurrentScaleMax;
		}
	}

	private void OnClearBatteryCurrentChart(object? sender, EventArgs e)
	{
		_batteryCurrentData.Clear();
	}
}

