using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop control operation store result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
/// <param name="Lease">The lease.</param>
public sealed record CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus Status, CustomLoopControlOperation? Operation, ICustomLoopControlOperationLease? Lease = null);
