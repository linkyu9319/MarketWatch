using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

/// <summary>
/// Shared validation logic for reminder rules across UI editing and JSON config import.
/// </summary>
public static class ReminderRuleValidator
{
    public static bool ValidateRules(IEnumerable<ReminderRule> rules, out string? errorMessage)
    {
        foreach (var rule in rules)
        {
            if (rule.TriggerTime >= rule.CutoffTime)
            {
                var displayName = string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name.Trim();
                errorMessage = $"提醒规则「{displayName}」的触发时间 ({rule.TriggerTime:HH:mm}) 必须早于截止时间 ({rule.CutoffTime:HH:mm})，请调整后再保存或导入。";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }
}
