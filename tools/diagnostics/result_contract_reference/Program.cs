Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ResultContract.Reference <fixture> <artifacts> <verdicts>");
    return 2;
}
return Alas.Core.Diagnostics.ContractCheck.Run(args[0], args[1], args[2]);
