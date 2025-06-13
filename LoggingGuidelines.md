# Serilog Logging Guidelines for InfoPanel

This document defines the logging standards for the InfoPanel application using Serilog.

## Logger Setup

Each class should declare its own logger instance to include the class name in log output:

```csharp
private static readonly ILogger Logger = Log.ForContext<ClassName>();
```

For static classes, use:
```csharp
private static readonly ILogger Logger = Log.ForContext(typeof(ClassName));
```

This ensures that the `[SourceContext]` property in logs includes the class name, making it easier to identify the source of log entries.

## Log Levels

### Debug
**Purpose:** Detailed diagnostic information useful during development and troubleshooting.

**When to use:**
- Detailed protocol/communication messages (e.g., sending/receiving data packets)
- Internal state changes and method flow
- Performance measurements and timing information
- Cache operations (hits/misses)
- Resource allocation/deallocation details

**Examples:**
```csharp
Logger.Debug("BeadaPanelDevice {Device}: Sent infoTag", device);
Logger.Debug("Disposing resources for {TaskName}", this.GetType().Name);
Logger.Debug("Font cache miss for {FontFamily}", fontFamily);
Logger.Debug("Render time: {ElapsedMs}ms for profile {ProfileId}", elapsed, profileId);
```

### Information
**Purpose:** General application flow and significant business events.

**When to use:**
- Application/service lifecycle events (start/stop)
- Successful device/plugin initialization
- Major configuration changes
- Successful completion of significant operations
- Connection established/closed events

**Examples:**
```csharp
Logger.Information("InfoPanel starting up");
Logger.Information("Started BeadaPanel device {Device}", device);
Logger.Information("{TaskName} Task stopped", this.GetType().Name);
Logger.Information("Plugin {PluginName} loaded successfully", pluginName);
Logger.Information("WebServer started on port {Port}", port);
```

### Warning
**Purpose:** Abnormal or unexpected events that don't prevent the application from functioning.

**When to use:**
- Missing optional resources or devices
- Fallback behavior activated
- Performance degradation detected
- Non-critical configuration issues
- Temporary failures that will be retried

**Examples:**
```csharp
Logger.Warning("BeadaPanelDevice {Device}: USB Device not found", device);
Logger.Warning("TuringPanelE: Screen not found on port {Port}", port);
Logger.Warning("Font fallback to {DefaultFont} for {RequestedFont}", "Segoe UI", fontFamily);
Logger.Warning("Shared memory not available for HWiNFO, retrying in {Seconds}s", 5);
```

### Error
**Purpose:** Error events that still allow the application to continue running.

**When to use:**
- Handled exceptions
- Failed operations that won't be retried
- Data validation failures
- Resource access failures
- Plugin failures

**Examples:**
```csharp
Logger.Error(ex, "BeadaPanelDevice {Device}: Exception during work", device);
Logger.Error(ex, "Failed to load plugin {PluginName}", pluginName);
Logger.Error(ex, "WebServerTask: Initialization error");
Logger.Error("Failed to parse color value: {ColorString}", colorString);
```

### Fatal
**Purpose:** Critical errors that will cause the application to terminate.

**When to use:**
- Unhandled exceptions
- Critical resource exhaustion
- Unrecoverable application state

**Examples:**
```csharp
Logger.Fatal(exception, "CurrentDomain_UnhandledException occurred. IsTerminating: {IsTerminating}", e.IsTerminating);
```

## Best Practices

### 1. Use Structured Logging
Always use message templates with parameters instead of string interpolation:

**Good:**
```csharp
Logger.Information("Device {DeviceId} connected at {Timestamp}", deviceId, DateTime.Now);
```

**Bad:**
```csharp
Logger.Information($"Device {deviceId} connected at {DateTime.Now}");
```

### 2. Include Relevant Context
Always include relevant identifiers and context in log messages:
- Device IDs
- Plugin names
- Task names
- Port numbers
- File paths
- User actions

### 3. Log Exceptions Properly
Always pass the exception as the first parameter when logging exceptions:

```csharp
try
{
    // operation
}
catch (Exception ex)
{
    Logger.Error(ex, "Failed to perform operation for device {DeviceId}", deviceId);
}
```

### 4. Avoid Logging Sensitive Information
Never log:
- Passwords or API keys
- Personal user information
- Full file paths containing usernames
- Sensitive business data

### 5. Performance Considerations
- Debug logs should be detailed but not overwhelming
- Avoid logging in tight loops without good reason
- Consider the performance impact of serializing complex objects

### 6. Consistency
- Use consistent message formats for similar operations
- Use consistent parameter names across the application
- Follow the same patterns for similar scenarios

## Common Patterns

### Task Lifecycle
```csharp
Logger.Debug("{TaskName} starting initialization", taskName);
Logger.Information("{TaskName} started successfully", taskName);
Logger.Information("{TaskName} stopping", taskName);
Logger.Information("{TaskName} stopped", taskName);
```

### Device Communication
```csharp
Logger.Debug("Sending {MessageType} to device {DeviceId}", messageType, deviceId);
Logger.Debug("Received {ResponseType} from device {DeviceId}", responseType, deviceId);
Logger.Information("Device {DeviceId} connected successfully", deviceId);
Logger.Warning("Device {DeviceId} not responding, attempt {Attempt}/{MaxAttempts}", deviceId, attempt, maxAttempts);
Logger.Error(ex, "Device {DeviceId} communication failed", deviceId);
```

### Plugin Operations
```csharp
Logger.Debug("Loading plugin from {Path}", pluginPath);
Logger.Information("Plugin {PluginName} version {Version} loaded", name, version);
Logger.Warning("Plugin {PluginName} is using deprecated API", name);
Logger.Error(ex, "Plugin {PluginName} threw exception during {Operation}", name, operation);
```

### Performance Logging
```csharp
Logger.Debug("Operation {OperationName} completed in {ElapsedMs}ms", operation, elapsed);
Logger.Warning("Operation {OperationName} took {ElapsedMs}ms, expected <{ExpectedMs}ms", operation, elapsed, expected);
```