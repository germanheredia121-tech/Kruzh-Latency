using KruzhLatency.Infra.Timer;

// ADR-010: residente del timer. Termina cuando la tarea programada lo detiene (o se cierra la sesión).
using var cancel = new CancellationTokenSource();
return TimerResidentAgent.Run(cancel.Token);
