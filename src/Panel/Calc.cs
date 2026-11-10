using System;
using System.Collections.Generic;
using System.Globalization;

// ======================================================================
//  Калькулятор і перетворення одиниць у рядку пошуку панелі.
//  Вираз: + - * / ^ % (остача), дужки, числа з крапкою чи комою, функції sqrt, sin, cos, tan
//  (у градусах), ln, log, abs, round, константи pi, e. Наприклад «2+2*3», «sqrt(2)^2», «15% of 80».
//  Одиниці: «10 km in mi», «100 f в c», «2 gb to mb», «90 km/h у m/s» (довжина, маса, температура,
//  дані, час, швидкість, об'єм, площа). Не вираз - TryEval повертає false, і пошук іде звично.
// ======================================================================
static class Calc
{
    public sealed class Unit { public string Kind; public double K, Off; public Unit(string kind, double k, double off) { Kind = kind; K = k; Off = off; } }

    static readonly Dictionary<string, Unit> units = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);

    static void U(string kind, double k, params string[] names) { foreach (string n in names) units[n] = new Unit(kind, k, 0); }

    static Calc()
    {
        // довжина (метри)
        U("len", 1e-3, "mm", "мм"); U("len", 1e-2, "cm", "см"); U("len", 1, "m", "м"); U("len", 1e3, "km", "км");
        U("len", 0.0254, "in", "inch", "дюйм", "\""); U("len", 0.3048, "ft", "фут"); U("len", 0.9144, "yd", "ярд");
        U("len", 1609.344, "mi", "mile", "миля", "милі"); U("len", 1852, "nmi", "морська");
        // маса (кілограми)
        U("mass", 1e-6, "mg", "мг"); U("mass", 1e-3, "g", "г"); U("mass", 1, "kg", "кг"); U("mass", 1000, "t", "т");
        U("mass", 0.45359237, "lb", "lbs", "фунт"); U("mass", 0.028349523125, "oz", "унція");
        // дані (байти; КБ = 1024, як у Провіднику)
        U("data", 1, "b", "byte", "б", "байт"); U("data", 1024, "kb", "кб"); U("data", 1048576, "mb", "мб");
        U("data", 1073741824, "gb", "гб"); U("data", 1099511627776, "tb", "тб");
        U("data", 0.125, "bit", "біт"); U("data", 125, "kbit", "кбіт"); U("data", 125000, "mbit", "мбіт"); U("data", 125000000, "gbit", "гбіт");
        // час (секунди)
        U("time", 1e-3, "ms", "мс"); U("time", 1, "s", "sec", "с", "сек"); U("time", 60, "min", "хв");
        U("time", 3600, "h", "hr", "год"); U("time", 86400, "d", "day", "days", "дн", "доба"); U("time", 604800, "week", "тиж");
        // швидкість (м/с)
        U("speed", 1, "m/s", "м/с"); U("speed", 1 / 3.6, "km/h", "kmh", "км/год"); U("speed", 0.44704, "mph", "миль/год"); U("speed", 0.514444, "kn", "вузол");
        // об'єм (літри)
        U("vol", 1e-3, "ml", "мл"); U("vol", 1, "l", "л"); U("vol", 1000, "m3", "м3", "м³");
        U("vol", 3.785411784, "gal", "галон"); U("vol", 0.0295735, "floz");
        // площа (м²)
        U("area", 1, "m2", "м2", "м²"); U("area", 1e4, "ha", "га"); U("area", 1e6, "km2", "км2", "км²"); U("area", 4046.8564224, "acre", "акр");
        U("area", 1e-4, "cm2", "см2", "см²"); U("area", 0.09290304, "ft2", "фут2");
        // температура (кельвіни: значення * K + Off)
        units["c"] = units["°c"] = units["с°"] = units["цельсій"] = new Unit("temp", 1, 273.15);
        units["f"] = units["°f"] = units["фаренгейт"] = new Unit("temp", 5.0 / 9.0, 273.15 - 32 * 5.0 / 9.0);
        units["k"] = units["кельвін"] = new Unit("temp", 1, 0);
    }

    static readonly string[] Seps = { " in ", " to ", " в ", " у ", " -> ", " => " };

    // вираз або перетворення; result - готовий до показу текст (числа - за мовою системи)
    public static bool TryEval(string q, out string result)
    {
        result = null;
        if (string.IsNullOrEmpty(q)) return false;
        q = q.Trim();
        if (q.Length < 2 || q.Length > 200) return false;
        try
        {
            if (Convert(q, out result)) return true;
            // хоча б одна цифра й один оператор (а не просто число чи слово)
            bool digit = false, op = false;
            foreach (char c in q)
            {
                if (char.IsDigit(c)) digit = true;
                else if ("+-*/^%(".IndexOf(c) >= 0) op = true;
            }
            string lq = q.ToLowerInvariant();
            if (lq.StartsWith("sqrt") || lq.StartsWith("sin") || lq.StartsWith("cos") || lq.StartsWith("tan") || lq.StartsWith("ln") || lq.StartsWith("log") || lq.StartsWith("abs") || lq.StartsWith("round")) op = true;
            if (!digit || !op) return false;
            string e = q.TrimStart('=');
            // «15% of 80» / «15% від 80»
            foreach (string w in new string[] { "% of ", "% від " })
            {
                int k = lq.IndexOf(w, StringComparison.Ordinal);
                if (k > 0) e = "(" + q.Substring(0, k) + ")/100*(" + q.Substring(k + w.Length) + ")";
            }
            Parser p = new Parser(e);
            double v = p.Expr();
            if (!p.End || double.IsNaN(v) || double.IsInfinity(v)) return false;
            result = Fmt(v);
            return true;
        }
        catch { return false; }
    }

    static bool Convert(string q, out string result)
    {
        result = null;
        string lq = " " + q.ToLowerInvariant() + " ";
        foreach (string s in Seps)
        {
            int k = lq.IndexOf(s, StringComparison.Ordinal);
            if (k <= 0) continue;
            string left = lq.Substring(1, k - 1).Trim(), to = lq.Substring(k + s.Length).Trim();
            // число й одиниця: «10km», «10 km», «-5 °c»
            int i = 0;
            while (i < left.Length && (char.IsDigit(left[i]) || left[i] == '.' || left[i] == ',' || left[i] == '-' || left[i] == '+' || left[i] == ' ')) i++;
            if (i == 0) return false;
            double v;
            string num = left.Substring(0, i).Replace(" ", "").Replace(',', '.');
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            Unit a, b;
            string from = left.Substring(i).Trim();
            if (!units.TryGetValue(from, out a) || !units.TryGetValue(to, out b) || a.Kind != b.Kind) return false;
            double r = ((v * a.K + a.Off) - b.Off) / b.K;
            result = Fmt(r) + " " + to;
            return true;
        }
        return false;
    }

    static string Fmt(double v)
    {
        double r = Math.Round(v, 10);
        if (Math.Abs(r) >= 1e15 || (Math.Abs(r) < 1e-6 && r != 0)) return r.ToString("0.######E+0", CultureInfo.CurrentCulture);
        return r.ToString("#,0.##########", CultureInfo.CurrentCulture);
    }

    // рекурсивний спуск: Expr = Term {(+|-) Term}; Term = Pow {(*|/|%) Pow}; Pow = Unary [^ Pow]; Unary = [-+] Atom
    sealed class Parser
    {
        readonly string s;
        int i;
        public Parser(string text) { s = text.Replace(" ", "").Replace('×', '*').Replace('÷', '/').Replace(':', '/'); }
        public bool End { get { return i >= s.Length; } }

        char Peek { get { return i < s.Length ? s[i] : '\0'; } }

        public double Expr()
        {
            double v = Term();
            while (Peek == '+' || Peek == '-') { char o = s[i++]; double r = Term(); v = o == '+' ? v + r : v - r; }
            return v;
        }

        double Term()
        {
            double v = Pow();
            while (Peek == '*' || Peek == '/' || Peek == '%')
            {
                char o = s[i++];
                double r = Pow();
                v = o == '*' ? v * r : (o == '/' ? v / r : v % r);
            }
            return v;
        }

        double Pow()
        {
            double v = Unary();
            if (Peek == '^') { i++; return Math.Pow(v, Pow()); }
            return v;
        }

        double Unary()
        {
            if (Peek == '-') { i++; return -Unary(); }
            if (Peek == '+') { i++; return Unary(); }
            return Atom();
        }

        double Atom()
        {
            if (Peek == '(') { i++; double v = Expr(); if (Peek != ')') throw new FormatException(); i++; return v; }
            int st = i;
            if (char.IsDigit(Peek) || Peek == '.' || Peek == ',')
            {
                while (char.IsDigit(Peek) || Peek == '.' || Peek == ',') i++;
                if (Peek == 'e' || Peek == 'E')
                {
                    int save = i; i++;
                    if (Peek == '+' || Peek == '-') i++;
                    if (char.IsDigit(Peek)) while (char.IsDigit(Peek)) i++; else i = save;
                }
                return double.Parse(s.Substring(st, i - st).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            while (char.IsLetter(Peek)) i++;
            string name = s.Substring(st, i - st).ToLowerInvariant();
            if (name == "pi" || name == "π") return Math.PI;
            if (name == "e") return Math.E;
            if (name.Length == 0 || Peek != '(') throw new FormatException();
            i++;
            double a = Expr();
            if (Peek != ')') throw new FormatException();
            i++;
            switch (name)
            {
                case "sqrt": return Math.Sqrt(a);
                case "sin": return Math.Sin(a * Math.PI / 180);
                case "cos": return Math.Cos(a * Math.PI / 180);
                case "tan": return Math.Tan(a * Math.PI / 180);
                case "ln": return Math.Log(a);
                case "log": return Math.Log10(a);
                case "abs": return Math.Abs(a);
                case "round": return Math.Round(a);
                default: throw new FormatException();
            }
        }
    }
}
