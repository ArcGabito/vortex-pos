namespace VortexPos.Core.Domain;

/// <summary>
/// Etiquetas de peso variable que imprime la balanza: EAN-13 del rango de uso interno (empieza con 2). Después del
/// prefijo de dos dígitos (20 a 29) va el código del artículo en la balanza (PLU, por defecto 5 dígitos), el valor (lo
/// que sobra hasta completar 12) y el dígito de control. El valor es el peso en gramos o el precio en céntimos, según
/// la configuración de la tienda.
/// </summary>
public static class ScaleLabel
{
    public const string Weight = "weight";
    public const string Price = "price";

    public static readonly string[] Modes = [Weight, Price];

    /// <summary>Dígitos del PLU que se aceptan (el valor usa los que quedan: 6 a 4).</summary>
    public const int MinItemDigits = 4, MaxItemDigits = 6;

    /// <summary>Separa el PLU y el valor si <paramref name="code"/> es una etiqueta válida (13 dígitos, prefijo 2, control).</summary>
    public static bool TryParse(string? code, int itemDigits, out string plu, out int value)
    {
        plu = "";
        value = 0;
        code = code?.Trim();
        if (code is not { Length: 13 } || code[0] != '2' || !code.All(char.IsAsciiDigit) ||
            itemDigits is < MinItemDigits or > MaxItemDigits || !CheckDigitOk(code))
            return false;
        plu = code.Substring(2, itemDigits);
        value = int.Parse(code.AsSpan(2 + itemDigits, 10 - itemDigits));
        return true;
    }

    /// <summary>Dígito de control EAN-13: pesos 1 y 3 alternados sobre los 12 primeros.</summary>
    public static bool CheckDigitOk(string code)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (code[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == code[12] - '0';
    }

    /// <summary>Arma una etiqueta (para pruebas y demos): calcula el dígito de control.</summary>
    public static string Build(string prefix, string plu, int value, int itemDigits = 5)
    {
        var body = prefix + plu.PadLeft(itemDigits, '0') + value.ToString().PadLeft(10 - itemDigits, '0');
        for (var d = 0; d <= 9; d++)
            if (CheckDigitOk(body + d))
                return body + d;
        throw new InvalidOperationException();
    }
}
