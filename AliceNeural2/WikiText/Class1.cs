using System.Text;
using MwParserFromScratch;
using MwParserFromScratch.Nodes;
namespace WikiText
{
    public static class WikitextHelper
    {
        public static string WikiTextToReadableTextNoSpace(this string wikitext)
        {
            //https://github.com/CXuesong/MwParserFromScratch
            var parser = new WikitextParser();
            var ast = parser.Parse(wikitext);
            StringBuilder sb = new();
            foreach (var line in ast.Lines)
            {
                if (line.GetType() == typeof(Paragraph) && line.ToPlainText().StartsWith("[["))
                    continue;//non inserisco i paragrafi delle illustrazioni
                var children = line.EnumChildren();
                foreach (var child in children)
                {
                    string childText = child.ToPlainText();
                    if (string.IsNullOrWhiteSpace(childText) || childText.Equals("\n"))
                    {
                        continue;
                    }
                    else if (child.GetType() == typeof(Template))//in questo caso si effettua un parsing manuale, ad esempio per gestire: https://www.mediawiki.org/wiki/Help:Magic_words#Formatting
                    {
                        string? elemento = child.ToString();
                        List<string> paroleChiave = ["vedi"];
                        string? result = elemento != null ? paroleChiave.FirstOrDefault((_) => elemento.Contains(_, StringComparison.CurrentCultureIgnoreCase)) : null;
                        if (elemento != null && result == null)//il child non contiene le parole chiave
                        {
                            //posso inserire il contenuto
                            int startIndex = elemento.IndexOfAny([' ', ':']);//l'ordine conta
                            startIndex = (startIndex == -1) ? elemento.LastIndexOf('{') : startIndex;//se non trovo il punto di inizio del contenuto assumo che ci siano due {{
                            int stopIndex = elemento.IndexOfAny(['|', '}']);//l'ordine conta
                            int lunghezzaContenuto = stopIndex - startIndex - 1;
                            string contenuto = elemento.Substring(startIndex + 1, lunghezzaContenuto);
                            sb.Append(contenuto);
                        }
                    }
                    else if (child.GetType() == typeof(WikiLink))//quando è un WikiLink non stampo i riferimenti a file, thumb e alt, etc.
                    {
                        List<string> paroleChiave = ["file", "thumb", "alt"];
                        string? result = paroleChiave.FirstOrDefault((_) => childText.Contains(_, StringComparison.CurrentCultureIgnoreCase));
                        if (result == null)//il childText non contiene le parole chiave
                        {
                            sb.Append(childText);
                        }
                    }
                    else //child.GetType()!= WikiLink && child.GetType()!=Template
                    {
                        sb.Append(childText);
                    }
                }
            }
            return sb.ToString();
        }
    }
}
