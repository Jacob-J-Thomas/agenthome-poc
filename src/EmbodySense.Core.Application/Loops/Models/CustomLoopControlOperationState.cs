using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop control operation state values.
/// </summary>
public enum CustomLoopControlOperationState
{
    /// <summary>
    /// Identifies the unknown custom loop control operation state.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pending custom loop control operation state.
    /// </summary>
    Pending = 1,
    /// <summary>
    /// Identifies the complete custom loop control operation state.
    /// </summary>
    Complete = 2
}
