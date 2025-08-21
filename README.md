# Electrical Impedance Tomography

This repository contains an experimental .NET solution for Electrical Impedance Tomography (EIT).  It is structured as a multi–project workspace consisting of a cross‑platform UI built with **.NET MAUI** and several supporting libraries.

## Projects

| Project | Description |
| ------- | ----------- |
| **ElectricalImpedanceTomography** | MAUI application providing the user interface |
| **BusinessLayer** | Core business logic for reconstructions and simulations |
| **DataAccessLayer** | Handles data acquisition from the DAQ hardware |
| **ServiceLayer** | Exposes high level services used by the UI |
| **Utility** | Mathematical models, solvers and helpers |

All projects target **.NET 9**.

## Building

The solution file `ElectricalImpedanceTomography.sln` can be built with the `dotnet` CLI or Visual Studio 2022+.  Example:

```bash
# Restore packages and build all projects
 dotnet build ElectricalImpedanceTomography.sln
```

## Configuration

A sample `config.json` file is located in `ElectricalImpedanceTomography/Resources/Raw/`.  It specifies the serial port settings used by the Data Access Layer when communicating with the DAQ device.

```
{
  "PortName": "COM1",
  "BaudRate": "115200",
  "Parity": "None",
  "DataBits": 8
}
```

## Status

This code base is under active development and many components are still experimental.  Contributions and bug reports are welcome.


## Validation

A lightweight validation suite compares analytic reference solutions against the numerical solvers.  Current reference cases include

- Fourier boundary currents in the unit disc with analytic potential \(u(r,\theta)=\frac{1}{\sigma_0}\sum r^n/n(\alpha_n\cos n\theta+\beta_n\sin n\theta)\),
- Dipole sources with \(u(x)=\frac{I}{2\pi\sigma_0}\log\frac{|x-a|}{|x-b|}\),
- Small circular inclusions with polarization tensor \(M=2\pi a^2\frac{\sigma_1-\sigma_0}{\sigma_1+\sigma_0}\).

The checks also evaluate internal operators (e.g., FEM gradient calculations) and report relative errors so differences are easy to spot.  Helper modules live under `Utility/Tests/Validation` and are executed automatically when the app starts.  To run them manually from any host project, invoke:

```csharp
Utility.Tests.Validation.ValidationSelfTests.RunAll();
```

The error metrics operate directly on `PotentialDistribution` and `ConductivityDistribution` objects, so solver outputs can be compared without manual extraction. Failures are written to the debug output stream; no external test runner is required.
