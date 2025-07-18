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

