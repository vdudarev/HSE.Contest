﻿using HSE.Contest.ClassLibrary;
using HSE.Contest.ClassLibrary.Communication.Requests;
using HSE.Contest.ClassLibrary.Communication.Responses;
using HSE.Contest.ClassLibrary.DbClasses;
using HSE.Contest.ClassLibrary.DbClasses.TestingSystem;
using HSE.Contest.ClassLibrary.TestsClasses.FunctionalTest;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace NetFrameworkFunctionalTesterService.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class FunctionalTesterController
    {
        private readonly HSEContestDbContext _db;

        public FunctionalTesterController(HSEContestDbContext db)
        {
            _db = db;
        }

        [HttpPost]
        public async Task<TestResponse> TestProject([FromBody] TestRequest request)
        {
            try
            {
                var solution = _db.Solutions.Find(request.SolutionId);

                if (solution is null)
                {
                    return new TestResponse
                    {
                        OK = false,
                        Message = "no solution found",
                        Result = ResultCode.IE,
                        TestId = request.TestId,
                    };
                }

                var compilation = _db.CompilationResults.Find(request.SolutionId);

                if (compilation is null)
                {
                    return new TestResponse
                    {
                        OK = false,
                        Message = "no compilation found",
                        Result = ResultCode.IE,
                        TestId = request.TestId,
                    };
                }

                if (compilation.ResultCode != ResultCode.OK || compilation.File is null)
                {
                    return new TestResponse
                    {
                        OK = false,
                        Message = "no compilation file found",
                        Result = ResultCode.IE,
                        TestId = request.TestId,
                    };
                }
                else
                {
                    string dirPath = "/home/solution";

                    if (Directory.Exists(dirPath))
                    {
                        Directory.Delete(dirPath, true);
                    }

                    var dir = Directory.CreateDirectory(dirPath);
                    string fullPath = dirPath + "/" + compilation.File.Name;

                    System.IO.File.WriteAllBytes(fullPath, compilation.File.Content);
                    ZipFile.ExtractToDirectory(fullPath, dirPath, true);

                    var pathToDll = FindExeFile(dir);

                    TestingResult result;
                    if (pathToDll == null)
                    {
                        dir.Delete(true);

                        result = new TestingResult
                        {
                            SolutionId = solution.Id,
                            TestId = request.TestId,
                            Commentary = "No dll file found!",
                            ResultCode = ResultCode.RE,
                        };

                        return DbWriter.WriteToDb(_db, result, request.ReCheck);
                    }

                    var resp = await TestProject(pathToDll, request.TestId);
                    resp.OK = true;

                    dir.Delete(true);

                    result = new TestingResult
                    {
                        SolutionId = solution.Id,
                        TestId = request.TestId,
                        Commentary = resp.Commentary,
                        ResultCode = resp.Result,
                        Score = resp.Score,
                        TestData = JsonConvert.SerializeObject(resp)
                    };

                    return DbWriter.WriteToDb(_db, result, request.ReCheck);
                }
            }
            catch (Exception e)
            {
                return new TestResponse
                {
                    OK = false,
                    Message = "Error occured: " + e.Message + (e.InnerException is null ? "" : " Inner: " + e.InnerException.Message),
                    Result = ResultCode.IE,
                    TestId = request.TestId,
                };
            }
        }
        async Task<FunctionalTestResult> TestProject(string pathToDll, int testId)
        {
            var task = _db.TaskTests.Find(testId);
            var data = JsonConvert.DeserializeObject<FunctionalTestData>(task.TestData);

            var tasks = data.FunctionalTests.Select(t => Task.Run(() =>
            {
                var result = SingleTest(t.Input, pathToDll, t.TimeLimit);
                return new SingleFunctTestResult(t.Output.Replace("\r\n", "\n"), result.Item1.Replace("\r\n", "\n"), result.Item2, t.Name, t.Input, result.Item3);
            }));

            var results = await Task.WhenAll(tasks);

            return new FunctionalTestResult { Results = results };
        }
        string FindExeFile(DirectoryInfo dir)
        {
            var f = dir.GetFiles().FirstOrDefault(f => f.Name.EndsWith(".exe"));
            if (f == null)
            {
                string res = null;
                foreach (var subDir in dir.GetDirectories())
                {
                    res = FindExeFile(subDir);
                    if (res != null)
                    {
                        break;
                    }
                }
                return res;
            }
            return f.FullName;
        }

        (string, string, ResultCode) SingleTest(string test, string pathToExe, int tl)
        {
            try
            {
                string[] input = test.Split('\n');
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = pathToExe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    //Arguments = "google.com"
                    //UserName = userName,
                    //Password = pswrd
                };
                Process pr = new Process
                {
                    StartInfo = info
                };

                var outputHandler = new ProcessOutputHandler();
                pr.OutputDataReceived += new DataReceivedEventHandler(outputHandler.StrOutputHandler);
                pr.ErrorDataReceived += new DataReceivedEventHandler(outputHandler.StrErrorHandler);

                var res = pr.Start();

                pr.BeginOutputReadLine();
                pr.BeginErrorReadLine();

                if (!res)
                {
                    return (null, "Couldn't start test process!", ResultCode.IE);
                }

                string addErr = null;

                try
                {
                    StreamWriter sw = pr.StandardInput;
                    foreach (string inp in input)
                    {
                        sw.WriteLine(inp);
                    }
                    sw.Close();
                }
                catch (Exception e)
                {
                    addErr = "Could not input data to process! Error:\n" + e.Message;
                }

                res = pr.WaitForExit(tl);
                if (res)
                {
                    pr.WaitForExit();
                }


                var strOutput = string.IsNullOrEmpty(outputHandler.strOutput) ? outputHandler.strOutput : outputHandler.strOutput.TrimEnd();
                var err = string.IsNullOrEmpty(outputHandler.err) ? outputHandler.err : outputHandler.err.TrimEnd();
                err = string.IsNullOrEmpty(addErr) ? err : addErr + "\nOther errors from sterr:\n" + err;

                if (!res)
                {
                    pr.Kill();
                    return (strOutput, err + "\nTime Limit!", ResultCode.TL);
                }

                return (strOutput, err, ResultCode.OK);
            }
            catch (OutOfMemoryException e)
            {
                return ("", "Memory Limit!\n" + e.Message, ResultCode.ML);
            }
            catch (Exception e)
            {
                return ("", e.Message, ResultCode.IE);
            }
        }
    }
}
