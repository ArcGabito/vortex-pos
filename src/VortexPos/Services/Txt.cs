using VortexApi.Suite.Text;

namespace VortexPos.Services;

/// <summary>
/// Textos de la interfaz en el idioma elegido (español, inglés o portugués). Los textos de la app están en
/// <c>VortexPos.Core/Texts/{es,en,pt}.json</c> y los comunes de la suite en <c>VortexApi.Suite</c>. Se usa como
/// <c>@T(clave)</c> en Razor y <c>T(clave, args…)</c> en C#.
/// </summary>
public static class Txt
{
    public static TextCatalog Catalog => VortexPos.Core.Texts.CoreText.Catalog;

    public static string T(string key, params object?[] args) => Catalog.Get(Fmt.Language, key, args);
}
