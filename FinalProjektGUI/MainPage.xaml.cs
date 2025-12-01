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
	private const double TripThreshold = 0.9; // mA (discharge current)
	private const double LowVoltageThreshold = 1.9; // V
	private const double RecoveryVoltageThreshold = 2.2; // V
	private bool _isBatteryDead = false;

	// Battery Voltage Averaging
	private Queue<double> _batteryVoltageHistory = new Queue<double>();
	private const int BatteryVoltageAverageWindow = 6;

	// Traffic Light Mode
	private bool _isTrafficMode = false;
	private System.Timers.Timer? _trafficTimer;
	private int _trafficState = 0; // 0=Green, 1=Yellow, 2=Red
	private readonly int[] _trafficDurations = { 4000, 2000, 7000 }; // Green, Yellow, Red in ms

	// Graph Data Collections
	private ObservableCollection<ObservablePoint> _solarData = new();
	private ObservableCollection<ObservablePoint> _batteryData = new();
	private ObservableCollection<ObservablePoint> _totalLoadData = new();
	private ObservableCollection<ObservablePoint> _yellowData = new();
	private ObservableCollection<ObservablePoint> _redData = new();
	private ObservableCollection<ObservablePoint> _greenData = new();
	private int _timeWindowSeconds = 10; // Default 10 seconds
	private int _currentScaleMax = 4; // Default 4mA max

	// Keep track of start time for X-axis
	private DateTime _graphStartTime = DateTime.Now;
	private List<Axis> _allXAxes = new();
	private List<Axis> _allCurrentYAxes = new();

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
		TimeWindowPicker.SelectedIndex = 0; // Default to 10 seconds
		CurrentScalePicker.SelectedIndex = 3; // Default to 4mA
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

				// Calculate checksum (sum of all ASCII characters from after ### up to but not including checksum)
				int calculatedChecksum = 0;
				for (int i = 3; i < index; i++) // Start after "###", stop before checksum
				{
					calculatedChecksum += (int)packet[i];
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

			// Calculate derived values
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
				if (batteryVoltage < LowVoltageThreshold)
				{
					_isBatteryDead = true;
				}
				else if (batteryVoltage >= RecoveryVoltageThreshold)
				{
					_isBatteryDead = false;
				}

				// Battery Status (ADC5 - ADC4) / 100
				double batteryCurrent = (avgValues[5] - avgValues[4]) / 100.0;
				
				if (_isBatteryDead)
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
				AddGraphData(solarVoltage, batteryVoltage, totalLoad, yellowCurrent, redCurrent, greenCurrent);

				// Check for trip conditions (latching)
				if (!_isTripped)
				{
					// Low voltage trip
					if (batteryVoltage < LowVoltageThreshold)
					{
						_isTripped = true;
						TripCircuit();
						AddToLog($"TRIP: Low battery voltage ({batteryVoltage:F3} V < {LowVoltageThreshold} V)");
					}
					// Discharge overcurrent trip
					else if (batteryCurrent < 0 && Math.Abs(batteryCurrent) > TripThreshold)
					{
						_isTripped = true;
						TripCircuit();
						AddToLog($"TRIP: Battery discharge overcurrent ({Math.Abs(batteryCurrent):F1} mA > {TripThreshold} mA)");
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

	private void SendLedPacket()
	{
		try
		{
			if (_serialPort == null || !_serialPort.IsOpen)
			{
				return;
			}

			// Build LED state string (4 binary digits) - Active Low for LEDs, Active High for Blue LED
			// First digit: Blue LED (1 = ON when tripped, 0 = OFF normally)
			// Remaining digits: Yellow, Red, Green LEDs (0 = ON, 1 = OFF)
			string led1 = _isTripped ? "1" : "0"; // Blue LED - Active High
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
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Stop traffic timer if running
			if (_trafficTimer != null)
			{
				_trafficTimer.Stop();
				_trafficTimer.Dispose();
				_trafficTimer = null;
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

		// Re-enable LED switches
		Led2Switch.IsEnabled = true;
		Led3Switch.IsEnabled = true;
		Led4Switch.IsEnabled = true;

		// Update circuit status display
		CircuitStatusLabel.Text = "ON";
		CircuitStatusLabel.TextColor = Colors.Green;

		// Send packet to update LED states
		SendLedPacket();

		AddToLog("Circuit breaker RESET");
	}

	private void OnModeChanged(object? sender, ToggledEventArgs e)
	{
		_isTrafficMode = e.Value;

		if (_isTrafficMode)
		{
			// Disable manual LED switches
			Led2Switch.IsEnabled = false;
			Led3Switch.IsEnabled = false;
			Led4Switch.IsEnabled = false;

			// Start traffic light cycle
			_trafficState = 0;
			StartTrafficCycle();
			AddToLog("Traffic mode ENABLED");
		}
		else
		{
			// Stop traffic timer
			if (_trafficTimer != null)
			{
				_trafficTimer.Stop();
				_trafficTimer.Dispose();
				_trafficTimer = null;
			}

			// Re-enable manual LED switches if not tripped
			if (!_isTripped)
			{
				Led2Switch.IsEnabled = true;
				Led3Switch.IsEnabled = true;
				Led4Switch.IsEnabled = true;
			}

			AddToLog("Traffic mode DISABLED");
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

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		
		if (_trafficTimer != null)
		{
			_trafficTimer.Stop();
			_trafficTimer.Dispose();
			_trafficTimer = null;
		}
		
		DisconnectSerial();
	}

	// Graph Methods
	private void InitializeGraphs()
	{
		var drawMargin = new LiveChartsCore.Measure.Margin(50, 10, 30, 30);
		_allXAxes.Clear();
		_allCurrentYAxes.Clear();

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
		var totalLoadYAxis = CreateCurrentAxis();
		_allCurrentYAxes.Add(totalLoadYAxis);
		TotalLoadChart.XAxes = new[] { totalLoadXAxis };
		TotalLoadChart.YAxes = new[] { totalLoadYAxis };
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
		var yellowYAxis = CreateCurrentAxis();
		_allCurrentYAxes.Add(yellowYAxis);
		YellowChart.XAxes = new[] { yellowXAxis };
		YellowChart.YAxes = new[] { yellowYAxis };
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
		var redYAxis = CreateCurrentAxis();
		_allCurrentYAxes.Add(redYAxis);
		RedChart.XAxes = new[] { redXAxis };
		RedChart.YAxes = new[] { redYAxis };
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
		var greenYAxis = CreateCurrentAxis();
		_allCurrentYAxes.Add(greenYAxis);
		GreenChart.XAxes = new[] { greenXAxis };
		GreenChart.YAxes = new[] { greenYAxis };
		GreenChart.DrawMargin = drawMargin;
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
			ForceStepToMin = true, // Force using the step size
			Name = "Time (seconds)",
			NamePaint = new SolidColorPaint(SKColors.White),
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			AnimationsSpeed = TimeSpan.FromMilliseconds(0),
			IsVisible = true
		};
	}

	private Axis CreateVoltageAxis()
	{
		return new Axis
		{
			MinLimit = 0,
			MaxLimit = 3.5,
			Name = "Voltage (V)",
			NamePaint = new SolidColorPaint(SKColors.White),
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 }
		};
	}

	private Axis CreateCurrentAxis()
	{
		return new Axis
		{
			MinLimit = 0,
			MaxLimit = _currentScaleMax,
			Name = "Current (mA)",
			NamePaint = new SolidColorPaint(SKColors.White),
			LabelsPaint = new SolidColorPaint(SKColors.LightGray),
			SeparatorsPaint = new SolidColorPaint(SKColors.Gray) { StrokeThickness = 1 },
			AnimationsSpeed = TimeSpan.FromMilliseconds(0) // No animation on axis
		};
	}

	private void AddGraphData(double solar, double battery, double totalLoad, double yellow, double red, double green)
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
				_totalLoadData.Add(new ObservablePoint(secondsElapsed, totalLoad));
				_yellowData.Add(new ObservablePoint(secondsElapsed, yellow));
				_redData.Add(new ObservablePoint(secondsElapsed, red));
				_greenData.Add(new ObservablePoint(secondsElapsed, green));

				// Debug logging
				AddToLog($"GRAPH DATA: Solar={solar:F3}V, Battery={battery:F3}V, Load={totalLoad:F1}mA, Y={yellow:F1}, R={red:F1}, G={green:F1} (Points: {_solarData.Count}, Time: {secondsElapsed:F1}s)");

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
					double minLimit = Math.Floor(rawMin / stepSize) * stepSize;
					double maxLimit = minLimit + _timeWindowSeconds;

					foreach (var axis in _allXAxes)
					{
						axis.MinLimit = minLimit;
						axis.MaxLimit = maxLimit;
					}

					// Keep data for up to 10 minutes (600 seconds), remove anything older
					double maxDataRetention = 600; // 10 minutes
					double cutoffSeconds = secondsElapsed - maxDataRetention;
					if (cutoffSeconds > 0)
					{
						RemoveOldData(_solarData, cutoffSeconds);
						RemoveOldData(_batteryData, cutoffSeconds);
						RemoveOldData(_totalLoadData, cutoffSeconds);
						RemoveOldData(_yellowData, cutoffSeconds);
						RemoveOldData(_redData, cutoffSeconds);
						RemoveOldData(_greenData, cutoffSeconds);
					}
				}
				else
				{
					// Still filling initial window
					foreach (var axis in _allXAxes)
					{
						axis.MinLimit = 0;
						axis.MaxLimit = _timeWindowSeconds;
					}
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
	}

	private void OnClearSolarChart(object? sender, EventArgs e)
	{
		_solarData.Clear();
		_graphStartTime = DateTime.Now;
	}

	private void OnClearBatteryChart(object? sender, EventArgs e)
	{
		_batteryData.Clear();
	}

	private void OnClearTotalLoadChart(object? sender, EventArgs e)
	{
		_totalLoadData.Clear();
	}

	private void OnClearYellowChart(object? sender, EventArgs e)
	{
		_yellowData.Clear();
	}

	private void OnClearRedChart(object? sender, EventArgs e)
	{
		_redData.Clear();
	}

	private void OnClearGreenChart(object? sender, EventArgs e)
	{
		_greenData.Clear();
	}

	private void OnCurrentScaleChanged(object? sender, EventArgs e)
	{
		if (CurrentScalePicker.SelectedIndex == -1)
			return;

		// Scale is 1-10 mA, index is 0-9
		_currentScaleMax = CurrentScalePicker.SelectedIndex + 1;

		// Update all current Y-axis limits
		foreach (var axis in _allCurrentYAxes)
		{
			axis.MaxLimit = _currentScaleMax;
		}
	}
}

