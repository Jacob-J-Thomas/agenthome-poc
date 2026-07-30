using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop control kind values.
/// </summary>
public enum CustomLoopControlKind
{
    /// <summary>
    /// Identifies the unknown custom loop control kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pause custom loop control kind.
    /// </summary>
    Pause = 1,
    /// <summary>
    /// Identifies the cancel custom loop control kind.
    /// </summary>
    Cancel = 2,
    /// <summary>
    /// Identifies the resume custom loop control kind.
    /// </summary>
    Resume = 3
}
