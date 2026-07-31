using Microsoft.Extensions.Options;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// TokenBudgetOptions 配置验证器
    /// </summary>
    public class TokenBudgetOptionsValidator : IValidateOptions<TokenBudgetOptions>
    {
        public ValidateOptionsResult Validate(string? name, TokenBudgetOptions options)
        {
            var errors = new List<string>();

            if (options.DefaultMaxContextTokens < 1000)
                errors.Add($"DefaultMaxContextTokens 至少为 1000，当前值: {options.DefaultMaxContextTokens}");

            ValidateThreshold(options.WarningThreshold, "WarningThreshold", errors);
            ValidateThreshold(options.CompactionThreshold, "CompactionThreshold", errors);

            if (options.CompactionThreshold.Percentage.HasValue && options.WarningThreshold.Percentage.HasValue &&
                options.CompactionThreshold.Percentage.Value <= options.WarningThreshold.Percentage.Value)
                errors.Add($"CompactionThreshold ({options.CompactionThreshold.Percentage}%) 必须大于 WarningThreshold ({options.WarningThreshold.Percentage}%)");

            if (options.SlidingWindowKeepTokens < 100)
                errors.Add($"SlidingWindowKeepTokens 至少为 100，当前值: {options.SlidingWindowKeepTokens}");

            if (options.SummaryTargetTokens < 100)
                errors.Add($"SummaryTargetTokens 至少为 100，当前值: {options.SummaryTargetTokens}");

            if (errors.Count > 0)
                return ValidateOptionsResult.Fail(errors);

            return ValidateOptionsResult.Success;
        }

        private static void ValidateThreshold(ThresholdOptions threshold, string name, List<string> errors)
        {
            if (threshold.Percentage.HasValue && (threshold.Percentage.Value < 0 || threshold.Percentage.Value > 100))
                errors.Add($"{name}.Percentage 必须在 0-100 范围内，当前值: {threshold.Percentage}");
        }
    }
}
