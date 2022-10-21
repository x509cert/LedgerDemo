// This code is designed to be as simple as possible, not pulling in lots of libraries and frameworks such as EF and MVC.
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    ApplicationName = typeof(Program).Assembly.FullName,
    ContentRootPath = Directory.GetCurrentDirectory(),
    WebRootPath = Directory.GetCurrentDirectory()
});

var app = builder.Build();
app.UseRouting();
app.UseDefaultFiles();
app.UseStaticFiles();

string htmlIndexStart = File.ReadAllText("indexStart.html");
string htmlError = File.ReadAllText("error.html");
string htmlSuccess = File.ReadAllText("success.html");

// this code connects to SQL as the user of the web app
const string connString = @"Server=.;Database=WorldCup;Trusted_Connection=True;";

// root folder
app.MapGet("/", async context => {
    Console.WriteLine($"Connection from {context.Connection.RemoteIpAddress}");
    
    // get list of moneylines
    using var con = new SqlConnection(connString);
    con.Open();

    using var cmd = new SqlCommand("SELECT * from [dbo].[MoneyLine]", con);
    var rows = cmd.ExecuteReader();

    // create array of games from the moneyline data
    List<Game> games = new List<Game>();
    while(rows.Read()) {
        var game = new Game((int)rows.GetValue(0),
                            (string)rows.GetValue(1), 
                            (int)rows.GetValue(2), 
                            (int)rows.GetValue(3), 
                            (string)rows.GetValue(4), 
                            (int)rows.GetValue(5), 
                            (DateTime)rows.GetValue(6));
        games.Add(game);
    }

    // Build the resulting HTML on the fly!
    // One <TR> per game, multiple <TD>s
    var sbHtml = new StringBuilder(htmlIndexStart);
    const string TR=@"<TR>", TD = @"<TD>", SpanTD=@"<TD colspan=3>";
    const string EndTR = @"</TR>", EndTD = @"</TD>";
    const string Radio = @"<input type='radio' name='bet' value='{0}|{1}|{2}|{3}'>";
    foreach (var g in games) {

        // Country vs Country heading
        sbHtml.Append(TR);
            sbHtml.Append(SpanTD);
                var imgHomeFlag = @"<img src='img/" + g.HomeCountry.Replace(" ", "") + @".png' width=14>&nbsp;";
                var imgVisitFlag = @"<img src='img/" + g.VisitCountry.Replace(" ", "") + @".png' width=14>&nbsp;";
                sbHtml.Append(imgHomeFlag + g.HomeCountry + "  vs  " + imgVisitFlag + g.VisitCountry); // + " on " + g.GameDateTime);
            sbHtml.Append(EndTD);
        sbHtml.Append(EndTR);

        // The three sets of odds
        sbHtml.Append(TR);
            // Win
            sbHtml.Append(TD);
                sbHtml.Append(string.Format(Radio, g.MoneyLineID, g.HomeCountry, "W", g.HomeCountryOdds));
                sbHtml.Append(g.HomeCountry + " " + g.HomeCountryOdds);
            sbHtml.Append(EndTD);
            
            // Draw
            sbHtml.Append(TD);
                sbHtml.Append(string.Format(Radio, g.MoneyLineID, g.HomeCountry, "D", g.DrawOdds));
                sbHtml.Append("Draw " + g.DrawOdds);
            sbHtml.Append(EndTD);
            
            // Loss
            sbHtml.Append(TD);
                sbHtml.Append(string.Format(Radio, g.MoneyLineID, g.HomeCountry, "L", g.VisitCountryOdds));
                sbHtml.Append(g.VisitCountry + " " + g.VisitCountryOdds);
            sbHtml.Append(EndTD);
        sbHtml.Append(EndTR);

        sbHtml.Append("\n");
    }

    // close off the HTML page
    sbHtml.Append("</table><p></p><input type='submit' value='Place Bet'></form></body></html>");

    await context.Response.WriteAsync(sbHtml.ToString());
});

// place a bet and insert into SQL
app.MapGet("/placebet", async context => {
    string name = context.Request.Query["flname"];
    string amount = context.Request.Query["betamount"];
    string moneyline = context.Request.Query["bet"];

    Console.WriteLine($"Request for bet from {name} for {amount} on {moneyline}");

    string fName = "", lName = "";
    string Country = "", result = "";
    var odds = 0;
    var moneylineId = 0;
    var amount2 = 0;

    // this validates the three input args and if there're no errors, returns them in the ref args
    // moneyline is a string of four values separated by a '|'
    if (ValidateRequest(name,
                        amount,
                        moneyline,
                        ref fName,
                        ref lName,
                        ref moneylineId,
                        ref Country,
                        ref result,
                        ref odds,
                        ref amount2)) {
        var cs = connString;

        using var con = new SqlConnection(cs);
        con.Open();

        using (var cmd = new SqlCommand("usp_PlaceBet", con)) {
            cmd.Parameters.AddWithValue("@MoneylineID", moneylineId);
            cmd.Parameters.AddWithValue("@FirstName", fName);
            cmd.Parameters.AddWithValue("@LastName", lName);
            cmd.Parameters.AddWithValue("@Country", Country);
            cmd.Parameters.AddWithValue("@Bet", amount2);
            cmd.Parameters.AddWithValue("@Odds", odds);

            cmd.CommandType = CommandType.StoredProcedure;
            cmd.ExecuteNonQuery();
        }

        // add the receipt tp the resulting confirmation page
        string? digest = GetLedgerDigest(con);
        if (!string.IsNullOrEmpty(digest)) {
            using (var jsonDoc = JsonDocument.Parse(digest)) {
                JsonElement hash = jsonDoc.RootElement.GetProperty("hash");
                JsonElement txtime = jsonDoc.RootElement.GetProperty("last_transaction_commit_time");
                
                htmlSuccess = htmlSuccess.Replace("%R%", ("<b>Hash:</b> " + hash.ToString().Replace("0x", "") + "<br><b>Timestamp:</b> " + txtime.ToString())); 
            }
        } else {
            htmlSuccess = htmlSuccess.Replace("%R%","Receipt not available right now.");
        }

        await context.Response.WriteAsync(htmlSuccess);
    } else {
        await context.Response.WriteAsync(htmlError);
    }
});

// Get SQL Server version
app.MapGet("/version", async context => {
    string? v = GetSqlVersion();
    await context.Response.WriteAsync(string.IsNullOrEmpty(v) ? "Unable to get SQL Server version" : v);
});

bool ValidateRequest(string name,
                     string amount,
                     string moneyline,
                     ref string fName,
                     ref string lName,
                     ref int moneylineId,
                     ref string Country,
                     ref string result,
                     ref int odds,
                     ref int amount2) {

    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(amount) && !string.IsNullOrEmpty(moneyline)) {
        // Get user name
        string[] flname = name.Split(' ', 2);
        if (flname.Count() != 2)
            return false;

        // get bet amount
        UInt32 intAmount = 0;
        if (!UInt32.TryParse(amount, out intAmount))
            return false;

        // moneyline is four fields:
        // FirstName|LastName|Wager|Odds
        const int NUM_FIELDS = 4;
        string[] moneylineItems = moneyline.Split('|', NUM_FIELDS);
        if (moneylineItems.Count() != NUM_FIELDS)
            return false;

        if (!Int32.TryParse(amount, out amount2))
            return false;

        // get user's name
        fName = flname[0];
        lName = flname[1];

        // get bet details
        if (!Int32.TryParse(moneylineItems[0], out moneylineId))
            return false;

        // Home country and Win, Draw or Loss
        Country = moneylineItems[1];
        result = moneylineItems[2];

        // odds
        if (!Int32.TryParse(moneylineItems[3], out odds))
            return false;

        return true;
    }

    return false;
}

// SQL query to get SQL Server version
string? GetSqlVersion() {
    using var con = new SqlConnection(connString);
    con.Open();

    using var cmd = new SqlCommand("SELECT @@VERSION", con);
    return cmd.ExecuteScalar()?.ToString();
}

// Get the last tx digest
string? GetLedgerDigest(SqlConnection con) {
    using var cmd = new SqlCommand("sp_generate_database_ledger_digest", con);
    return cmd.ExecuteScalar()?.ToString();
}

// Start the web app
app.Run();

// struct to hold game details
public struct Game {
    public Game(int MoneyLineID, string HomeCountry, int HomeCountryOdds, int DrawOdds, string VisitCountry, int VisitCountryOdds, DateTime GameDateTime) {
        this.MoneyLineID = MoneyLineID;
        this.HomeCountry = HomeCountry;
        this.HomeCountryOdds = HomeCountryOdds;
        this.DrawOdds = DrawOdds;
        this.VisitCountry = VisitCountry;
        this.VisitCountryOdds = VisitCountryOdds;
        this.GameDateTime = GameDateTime;
    }
    public int MoneyLineID;
    public string HomeCountry;
    public int HomeCountryOdds;
    public int DrawOdds;
    public string VisitCountry;
    public int VisitCountryOdds;
    public DateTime GameDateTime;
}