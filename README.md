# NightKnight
A valiant guardian who locks up the castle at curfew

## Overview
NightKnight is a Windows application that helps enforce bedtime schedules by automatically locking your workstation and eventually logging you off at configured times. It runs in the system tray and provides notifications as bedtime approaches.

## Features
- Configurable bedtimes for each day of the week
- Warning notifications before bedtime
- Automatic workstation locking at bedtime
- Forced logoff after a grace period
- Bedtime cancellation option for special occasions
- Live configuration updates (no restart required)
- Activity logging for tracking compliance

## Configuration
The application uses a `Settings.json` file located in the same directory as the executable. This file is created automatically on first run with default settings, and can be modified while the application is running.

### Configuration Options
```json
{
  "bedtimes": {
    "Monday": "22:30",
    "Tuesday": "22:30",
    "Wednesday": "22:30",
    "Thursday": "22:30",
    "Friday": "23:30",
    "Saturday": "23:30",
    "Sunday": "22:30"
  },
  "warningMinutesBefore": 15,
  "toastRepeatMinutes": 5,
  "graceMinutesAfterLock": 5,
  "statsFile": "C:\Users\[username]\NightKnight\stats.csv"
}
```

- `bedtimes`: Specifies the bedtime for each day of the week in 24-hour format
- `warningMinutesBefore`: How many minutes before bedtime to start showing warnings
- `toastRepeatMinutes`: How often to repeat warning notifications
- `graceMinutesAfterLock`: How many minutes after locking before forcing logoff
- `statsFile`: Path to the CSV file where activity logs are stored

## Usage
The application runs in the system tray with the following options:
- **Cancel tonight only**: Temporarily disables bedtime enforcement for the current day
- **Reload config**: Manually reload the configuration file
- **Exit**: Close the application

## Technical Details
- Built with .NET 9.0 for Windows 10 (17763.0+)
- Uses Windows Forms for the system tray interface
- Implements Windows toast notifications via Microsoft.Toolkit.Uwp.Notifications
- Logs activity to a CSV file for tracking
