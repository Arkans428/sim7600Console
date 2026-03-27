# SIMCOM Voice Dialer (SIM7500 / SIM7600 Series)

## AI Assistance Disclosure

This project was developed with the assistance of AI tools to accelerate development, improve code quality, and explore design approaches.

### Scope of AI Assistance

AI was used as a development aid in the following areas:

* Generating initial code structures and boilerplate
* Assisting with AT command handling patterns and parsing logic
* Helping design class architecture and separation of concerns
* Providing suggestions for error handling and edge cases
* Assisting in documentation and README creation

### Human Oversight

All AI-generated code was:

* Reviewed and validated before integration
* Tested against real hardware (SIMCOM modules)
* Modified where necessary to ensure correctness and reliability

Final implementation decisions, debugging, and system design were performed manually.

### Notes

AI was used as a tool—not a replacement for understanding.
If something breaks, that responsibility still belongs to the human holding the keyboard.

## Description
A Windows-based C# application for controlling SIMCOM LTE modules, providing voice calling, real-time audio streaming, and SMS functionality via AT commands.

This project interfaces directly with SIMCOM modems over USB serial ports and bridges live audio using NAudio.

## Features
* Voice call control (dial, answer, hang up)
* Real-time audio streaming (microphone ↔ modem ↔ speakers)
* SMS send, receive, read, and delete
* Call waiting (enable/disable/query)
* Call forwarding (multiple conditions)
* Automatic COM port detection via USB device IDs
* AT command abstraction layer with URC parsing

## Project Structure
```
SIMCOMVoiceDialer/
│
├── Program.cs                # Console UI / entry point
├── SerialAudioPhone.cs      # High-level controller
├── ModemControl.cs          # AT command + serial handling
├── AudioBridge.cs           # Audio streaming (NAudio)
├── SmsManager.cs            # SMS handling + parsing
├── SmsMessage.cs            # SMS data model
├── CallForwardReason.cs     # Call forwarding enum
```
Key components:

* SerialAudioPhone – orchestrates modem, audio, and SMS
* ModemControl – handles AT communication
* AudioBridge – manages PCM audio streaming
* SmsManager – parses and manages SMS

## Requirements
### Hardware
* SIMCOM LTE module (SIM7500 / SIM7600 series)
* USB connection exposing:
* AT command port (e.g., MI_02)
* Audio port (e.g., MI_04)
* Microphone and speakers

### Software
* Windows 10/11
* .NET 6.0 or newer
* Visual Studio 2022 (recommended) or .NET CLI

NuGet package:
* NAudio

## Getting Started
### 1. Clone the Repository
```
git clone https://github.com/yourusername/SIMCOMVoiceDialer.git
cd SIMCOMVoiceDialer
```

### 2. Install Dependencies

If using .NET CLI:
```
dotnet add package NAudio
```
Or via Visual Studio:

* Right-click project → Manage NuGet Packages → Install NAudio

### 3. Connect the Modem

Plug in your SIMCOM device via USB.
The application automatically detects ports using:

* AT Port: VID_1E0E&PID_9005&MI_02
* Audio Port: VID_1E0E&PID_9005&MI_04

No manual COM configuration required.

### 4. Build the Project
Option A – Visual Studio
1. Open the solution/project
2. Set build target to x64 (recommended)
3. Press Build → Build Solution

Option B – .NET CLI
` dotnet build

### 5. Run the Application
Visual Studio

Press F5 or Start Without Debugging


` dotnet run

### Usage

Once running, you’ll see the main menu:
```
++++++++++ Main Menu ++++++++++
D - Dial
A - Answer
H - Hang up
F - Call forwarding
W - Call waiting
S - SMS menu
Q - Quit
```
### Example: Dial a Call
` Enter number to dial: +123456789

### Example: Send SMS
```
Enter recipient: +123456789
Enter SMS text: Hello from SIM7600
```
## Configuration
### Default Settings
* Baud rate: 115200
* Audio format: 8000 Hz, mono PCM
* Echo suppression factor: 0.5

### Initialization Commands

Executed automatically:

* AT+CLIP=1 (Caller ID)
* Audio gain configuration
* SMS text mode setup
* Network registration settings

## How It Works
* The modem communicates over UART/USB using AT commands (default 115200 baud)
* Audio is streamed through a secondary serial interface using PCM
* URCs (like RING, +CMT) are parsed in real-time
* NAudio handles microphone capture and speaker playback

## Known Limitations
* Windows-only (uses WMI for device detection)
* Assumes SIMCOM USB interface layout is consistent
* Uses blocking delays (Thread.Sleep) in some areas
* Basic phone number validation
* Limited retry/error recovery logic

## Troubleshooting
### Modem Not Detected
* Verify USB drivers are installed
* Check Device Manager for COM ports
* Confirm device IDs match expected values

### No Audio
* Ensure correct microphone/speaker devices are available
* Confirm modem supports PCM audio over USB
* Check that audio port is opening successfully

### SMS Not Receiving
Verify:
* AT+CNMI=2,2,0,0,0 is active
* Network registration is complete

## References
* SIMCOM AT Command Manual
* SIMCOM UART Application Note
* NAudio Documentation
