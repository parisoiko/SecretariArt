using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SecretariArt.Extensions;

// Amounts and percentages as people type them: "12.50", "12,50" (the Greek way), "€ 1250" or "1250". A comma is read
// as the decimal point, never as a thousands separator, so "12,50" can't silently become €1,250 (QA, P4). Anything
// else ambiguous ("1.250,00", "1,2,3") is refused with a message instead of being guessed.
public class DecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ValueProviderResult value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }
        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);

        string text = (value.FirstValue ?? "").Replace("€", "").Replace(" ", "").Replace(" ", "").Trim();
        if (text.Length == 0)
        {
            // Empty: null for decimal? ([Required] says what's missing); an error for a plain decimal.
            if (bindingContext.ModelType == typeof(decimal))
            {
                bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, Message(bindingContext));
            }
            return Task.CompletedTask;
        }

        // "1.250" or "1,250": thousands the Greek or the English way, or one and a quarter? Ask rather than guess (QA, P12).
        int separator = text.IndexOfAny(['.', ',']);
        if (separator > 0 && text.Length - separator - 1 == 3 && text.Count(c => c is '.' or ',') == 1 && text[(separator + 1)..].All(char.IsDigit))
        {
            string whole = text[..separator] + text[(separator + 1)..];
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName,
                $"“{value.FirstValue!.Trim()}” could mean {whole} or {text[..separator]}.{text[(separator + 1)..(separator + 3)]}. Write whichever you mean, with no thousands separator.");
            return Task.CompletedTask;
        }

        if (text.Count(c => c == ',') + text.Count(c => c == '.') <= 1)
        {
            text = text.Replace(',', '.');
            if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed))
            {
                bindingContext.Result = ModelBindingResult.Success(parsed);
                return Task.CompletedTask;
            }
        }

        bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, Message(bindingContext));
        return Task.CompletedTask;
    }

    // "Enter the amount as a number, e.g. 12.50." or, for a percentage, "e.g. 25 or 12.5" (QA, P13).
    private static string Message(ModelBindingContext context)
    {
        string? name = context.ModelMetadata.DisplayName?.Replace(" (€)", "").ToLowerInvariant();
        return name == null
            ? "Enter amounts as numbers, e.g. 12.50."
            : $"Enter the {name} as a number, e.g. {(name.Contains("percent") ? "25 or 12.5" : "12.50")}.";
    }
}

public class DecimalModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.UnderlyingOrModelType == typeof(decimal) ? new DecimalModelBinder() : null;
}
