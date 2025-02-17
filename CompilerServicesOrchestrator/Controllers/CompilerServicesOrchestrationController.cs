﻿using Docker.DotNet;
using Docker.DotNet.Models;
using HSE.Contest.ClassLibrary;
using HSE.Contest.ClassLibrary.Communication.Requests;
using HSE.Contest.ClassLibrary.Communication.Responses;
using HSE.Contest.ClassLibrary.DbClasses;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace CompilerServicesOrchestrator.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class CompilerServicesOrchestrationController : ControllerBase
    {
        private readonly TestingSystemConfig _config;
        private readonly HSEContestDbContext _db;
        public CompilerServicesOrchestrationController(HSEContestDbContext db, TestingSystemConfig config)
        {
            _db = db;
            _config = config;
        }

        //[HttpPost]
        //public async Task<CompilerResponse> CompileProjectDebug([FromForm] IFormFile file, [FromForm] int taskId, [FromForm] string isNetCore)
        //{
        //    var dataBytes = await file.GetBytes();
        //    var req = new OldCompilerRequest
        //    {
        //        TaskId = taskId,
        //        File = dataBytes,
        //        IsNetCore = isNetCore == "true",
        //        ShouldUpdateFiles = false
        //    };

        //    return await CompileProject(req);
        //}

        [HttpPost]
        public async Task<TestResponse> CompileProject([FromBody] TestRequest request)
        {
            var solution = _db.Solutions.Find(request.SolutionId);
            if (solution is null || solution.File is null)
            {
                return new TestResponse
                {
                    OK = false,
                    Message = "no solution found",
                    Result = ResultCode.IE,
                    TestId = request.TestId,
                };
            }

            if (_config.CompilerImages.ContainsKey(solution.FrameworkType))
            {
                return await CompileInNewContainer(_config.CompilerImages[solution.FrameworkType], request);
            }
            else
            {
                return new TestResponse
                {
                    OK = false,
                    Message = "no framework found",
                    Result = ResultCode.IE,
                    TestId = request.TestId,
                };
            }
        }

        async Task<TestResponse> CompileInNewContainer(ImageConfig imageConfig, TestRequest request)
        {
            try
            {
                DockerClient client = new DockerClientConfiguration().CreateClient();

                var createRes = await client.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = imageConfig.Name,
                    ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    {
                        "80/tcp",  default(EmptyStruct)
                    }
                },
                    HostConfig = new HostConfig
                    {
                        PublishAllPorts = true
                    },
                    Env = new List<string> { "db_connection=" + _config.DatabaseInfo.GetConnectionStringFrom(null) }
                }).ConfigureAwait(false);

                var id = createRes.ID;

                //await Task.Delay(3000);

                var startRes = await client.Containers.StartContainerAsync(id, null).ConfigureAwait(false);
                if (startRes)
                {
                    var inspectRes = await client.Containers.InspectContainerAsync(id).ConfigureAwait(false);

                    if (inspectRes.State.Running)
                    {
                        var port = inspectRes.NetworkSettings.Ports["80/tcp"][0].HostPort;

                        var serviceName = inspectRes.Name;

                        var result = await CompilationRequest(port, imageConfig.TestActionLink, request);

                        await client.Containers.KillContainerAsync(id, new ContainerKillParameters()).ConfigureAwait(false);

                        await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters()).ConfigureAwait(false);

                        return result;
                    }
                }

                return new TestResponse
                {
                    OK = false,
                    Message = "Couldn't start new compiler container!",
                    Result = ResultCode.IE,
                    TestId = request.TestId,
                };
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

        async Task<bool> CheckIfAlive(string port)
        {
            using var httpClient = new HttpClient();
            var link = "http://host.docker.internal:" + port + "/health";
            HttpResponseMessage response = await httpClient.GetAsync(link);

            if (response.IsSuccessStatusCode)
            {
                string apiResponse = await response.Content.ReadAsStringAsync();
                HealthResponse obj = JsonConvert.DeserializeObject<HealthResponse>(apiResponse);
                return obj.Status == "Healthy";
            }
            else
            {
                return false;
            }
        }

        async Task<TestResponse> CompilationRequest(string port, string link, TestRequest request)
        {
            var isAlive = await CheckIfAlive(port);

            if (isAlive)
            {
                using var httpClient = new HttpClient();
                using var form = JsonContent.Create(request);
                var url = "http://host.docker.internal:" + port + link;
                HttpResponseMessage response = await httpClient.PostAsync(url, form);
                string apiResponse = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<TestResponse>(apiResponse);
                }
                else
                {
                    return new TestResponse
                    {
                        OK = false,
                        Message = "Created compilation container replied with " + response.StatusCode,
                        Result = ResultCode.IE,
                        TestId = request.TestId,
                    };
                }
            }
            else
            {
                return new TestResponse
                {
                    OK = false,
                    Message = "Created compilation container Is Dead!",
                    Result = ResultCode.IE,
                    TestId = request.TestId,
                };
            }
        }



        [HttpPost]
        public async Task<CompilationResponse> CompileTaskProject([FromBody] CompilationRequest request)
        {            
            if (_config.CompilerImages.ContainsKey(request.Framework))
            {
                return await CompileTaskInNewContainer(_config.CompilerImages[request.Framework], request);
            }
            else
            {
                return new CompilationResponse
                {
                    OK = false,
                    Message = "no framework found",
                };
            }
        }

        async Task<CompilationResponse> CompileTaskInNewContainer(ImageConfig imageConfig, CompilationRequest request)
        {
            try
            {
                DockerClient client = new DockerClientConfiguration().CreateClient(new System.Version(1, 41));

                var createRes = await client.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = imageConfig.Name,
                    ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    {
                        "80/tcp",  default(EmptyStruct)
                    }
                },
                    HostConfig = new HostConfig
                    {
                        PublishAllPorts = true
                    },
                    Env = new List<string> { "db_connection=" + _config.DatabaseInfo.GetConnectionStringFrom(null) }
                }).ConfigureAwait(false);

                var id = createRes.ID;

                //await Task.Delay(3000);

                var startRes = await client.Containers.StartContainerAsync(id, null).ConfigureAwait(false);
                if (startRes)
                {
                    var inspectRes = await client.Containers.InspectContainerAsync(id).ConfigureAwait(false);

                    if (inspectRes.State.Running)
                    {
                        var port = inspectRes.NetworkSettings.Ports["80/tcp"][0].HostPort;

                        var serviceName = inspectRes.Name;

                        var result = await CompilationTaskRequest(port, imageConfig.TaskActionLink, request);

                        await client.Containers.KillContainerAsync(id, new ContainerKillParameters()).ConfigureAwait(false);

                        await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters()).ConfigureAwait(false);

                        return result;
                    }
                }

                return new CompilationResponse
                {
                    OK = false,
                    Message = "Couldn't start new compiler container!",

                };
            }
            catch (Exception e)
            {
                return new CompilationResponse
                {
                    OK = false,
                    Message = "Error occured: " + e.Message + (e.InnerException is null ? "" : " Inner: " + e.InnerException.Message),
                };
            }
        }

        async Task<CompilationResponse> CompilationTaskRequest(string port, string link, CompilationRequest request)
        {
            var isAlive = await CheckIfAlive(port);

            if (isAlive)
            {
                using var httpClient = new HttpClient();
                using var form = JsonContent.Create(request);
                var url = "http://host.docker.internal:" + port + link;
                HttpResponseMessage response = await httpClient.PostAsync(url, form);
                string apiResponse = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<CompilationResponse>(apiResponse);
                }
                else
                {
                    return new CompilationResponse
                    {
                        OK = false,
                        Message = "Created compilation container replied with " + response.StatusCode,
                    };
                }
            }
            else
            {
                return new CompilationResponse
                {
                    OK = false,
                    Message = "Created compilation container Is Dead!",
                };
            }
        }
    }
}
