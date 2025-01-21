# Tool for analyzing Roslyn Helix execution time

This tool will compare the actual execution time of work items in Helix compared to what our scheduling expected the execution time to be. To run this tool do the following.

First login using the Azure CLI and make sure that your @microsoft.com address is logged in. It's okay to be logged into multiple tenants, the tool will grab the correct tenant credentials.

```cmd
> az login
```

Then run this tool and provide a PR number

```cmd
> dotnet run -- 76828
```

It will output the actual vs. expected execution times:

```text
Comparing Test_Linux_Debug
	workitem_0 expected: 09:59 actual: 10:59 diff: 00:59
	workitem_1 expected: 09:52 actual: 10:03 diff: 00:10
	workitem_2 expected: 09:59 actual: 10:38 diff: 00:38
	workitem_3 expected: 02:15 actual: 03:02 diff: 00:47
Comparing Test_Windows_CoreClr_Release
	workitem_0 expected: 00:25 actual: 01:04 diff: 00:38
	workitem_1 expected: 09:59 actual: 10:57 diff: 00:57
	workitem_2 expected: 09:59 actual: 09:10 diff: 00:49
	workitem_3 expected: 06:38 actual: 07:13 diff: 00:34
	workitem_4 expected: 09:55 actual: 06:35 diff: 03:19
	workitem_5 expected: 09:58 actual: 11:02 diff: 01:03
	workitem_6 expected: 06:23 actual: 06:22 diff: 00:00
Comparing Test_Windows_CoreClr_UsedAssemblies_Debug
	workitem_0 expected: 02:38 actual: 02:46 diff: 00:07
	workitem_1 expected: 09:59 actual: 10:20 diff: 00:20
	workitem_2 expected: 06:34 actual: 05:41 diff: 00:53
	workitem_3 expected: 07:04 actual: 07:13 diff: 00:08
	workitem_4 expected: 09:59 actual: 08:59 diff: 01:00
```

