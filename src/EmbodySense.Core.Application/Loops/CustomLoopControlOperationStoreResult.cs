using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus Status, CustomLoopControlOperation? Operation);
