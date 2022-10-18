using System;
using System.Data;
using System.Data.SqlClient;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    ApplicationName = typeof(Program).Assembly.FullName,
    ContentRootPath = Directory.GetCurrentDirectory(),
    WebRootPath = Directory.GetCurrentDirectory()
});

var app = builder.Build();
app.UseRouting();
app.UseDefaultFiles();

string htmlIndex = File.ReadAllText("index.html");
string htmlError = File.ReadAllText("error.html");
string htmlSuccess = File.ReadAllText("success.html");

const string connString = @"Server=.;Database=WorldCup;Trusted_Connection=True;";

// root
app.MapGet("/", async context => {
    Console.WriteLine($"Connection from {context.Connection.RemoteIpAddress}");
    string file = context.Request.Path;
    Console.WriteLine($"Request for {file}");
    await context.Response.WriteAsync(htmlIndex);
});

// place a bet and insert into SQL
app.MapGet("/placebet", async context =>
{

    string name = context.Request.Query["flname"];
    string amount = context.Request.Query["betamount"];
    string whichbet = context.Request.Query["bet"];

    Console.WriteLine($"Request for bet from {name} for {amount} on {whichbet}");

    string fName = "", lName = "";
    string Country1 = "", Country2 = "", result = "";
    Int32 odds = 0;

    if (ValidateRequest(name, amount, whichbet, ref fName, ref lName, ref Country1, ref Country2, ref result, ref odds))
    {
        var cs = connString;

        using var con = new SqlConnection(cs);
        con.Open();

        var cmd = new SqlCommand("usp_PlaceBet", con);
        cmd.Parameters.AddWithValue("@MoneylineID", 100);
        cmd.Parameters.AddWithValue("@FirstName", fName);
        cmd.Parameters.AddWithValue("@LastName", lName);
        cmd.Parameters.AddWithValue("@Country", Country1);
        cmd.Parameters.AddWithValue("@Bet", amount);
        cmd.Parameters.AddWithValue("@Odds", odds);

        cmd.CommandType = CommandType.StoredProcedure;
        cmd.ExecuteNonQuery();

        await context.Response.WriteAsync(htmlSuccess);
    }
    else
    {
        await context.Response.WriteAsync(htmlError);
    }
});

app.MapGet("/img/{country}.png", async context => {
    string file = context.Request.Path;
    byte[] img = File.ReadAllBytes(Directory.GetCurrentDirectory() + file);
    Console.WriteLine($"Request for {file}");
    //await context.Response.
});

app.MapGet("/version", async context => {
    string? v = GetSqlVersion();
    await context.Response.WriteAsync(v);
});

bool ValidateRequest(string name, string amount, string whichbet, 
                    ref string fName, ref string lName, 
                    ref string Country1, ref string Country2, 
                    ref string result, ref Int32 betAmount) {

    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(amount) || string.IsNullOrEmpty(whichbet))
        return false;
    
    string [] flname = name.Split(' ', 2);
    if (flname.Count() !=2)
        return false;

    UInt32 intAmount = 0;
    if (!UInt32.TryParse(amount, out intAmount))
        return false;

    string [] whichBetItems = whichbet.Split('|', 4);
    if (whichBetItems.Count() != 4)
        return false;
    
    Int32 odds = 0;
    if (!Int32.TryParse(whichBetItems[3], out odds))
        return false;
    
    fName = flname[0];
    lName = flname[1];
    Country1 = whichBetItems[0];
    Country2 = whichBetItems[1];
    result = whichBetItems[2];
    betAmount = odds;
    
    return true;
}

string? GetSqlVersion() {
    using var con = new SqlConnection(connString);
    con.Open();

    using var cmd = new SqlCommand("SELECT @@VERSION", con);
    return cmd.ExecuteScalar()?.ToString();
}

app.Run();
