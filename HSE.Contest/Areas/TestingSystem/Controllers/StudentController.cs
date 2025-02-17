﻿using HSE.Contest.Areas.TestingSystem.ViewModels;
using HSE.Contest.ClassLibrary;
using HSE.Contest.ClassLibrary.Communication.Requests;
using HSE.Contest.ClassLibrary.DbClasses;
using HSE.Contest.ClassLibrary.DbClasses.Administration;
using HSE.Contest.ClassLibrary.DbClasses.TestingSystem;
using HSE.Contest.ClassLibrary.RabbitMQ;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace HSE.Contest.Areas.TestingSystem.Controllers
{
    [Authorize(Roles = "student")]
    [Area("TestingSystem")]
    public class StudentController : TestingSystemController
    {
        private readonly string curId;
        private readonly bool isProff;
        public StudentController(UserManager<User> userManager, HSEContestDbContext db, TestingSystemConfig config, IHttpContextAccessor httpContextAccessor) : base(db, config)
        {
            curId = userManager.GetUserId(httpContextAccessor.HttpContext.User);
            var curUser = userManager.GetUserAsync(httpContextAccessor.HttpContext.User).Result;
            isProff = userManager.IsInRoleAsync(curUser, "professor").Result;
        }

        public IActionResult Index()
        {
            return RedirectToAction("AllTasks");
        }              

        public IActionResult AllTasks()
        {
            var tasks = _db.StudentTasks.Where(t => t.Group.Users.Select(s => s.UserId).Contains(curId) && t.From <= DateTime.Now)
            .Select(t => new TaskViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Description = $"Длительность: {(t.To - t.From).TotalMinutes} минут с {t.From} до {t.To}"
            }).OrderByDescending(m => m.Id).ToList();

            return View(tasks);
        }

        public IActionResult SendStudentSolution(int id)
        {
            var res = GetStudentTaskView(id, curId);

            if (res is null)
            {
                return NotFound();
            }

            //JsonSerializerSettings serializerSettings = new JsonSerializerSettings
            //{
            //    ContractResolver = new CamelCasePropertyNamesContractResolver(),
            //    DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind
            //};

            //return Content(JsonConvert.SerializeObject(res, serializerSettings));

            return View(res);
        }       

       

        [Authorize(Roles = "student, professor")]
        public IActionResult ViewSolutionReport(int id, string from)
        {
            var s = _db.Solutions.Find(id);

            if (s is null)
            {
                return NotFound();
            }

            var t = _db.StudentTasks.Find(s.TaskId);
           
            if (!isProff && CanSend(t, curId))
            {
                return Forbid();
            }

            var testResults = _db.TestingResults.Where(tr => tr.SolutionId == id).Join(_db.TaskTests, i => i.TestId, o => o.Id, (i, o) => new TestingResultViewModel(i, o)).ToArray();

            var res = new SolutionReportViewModel
            {
                Id = s.Id,
                TotalScore = s.Score,
                Result = s.ResultCode.ToString(),
                Time = s.Time,
                TestingResults = testResults,
                TaskId = s.TaskId,
                StudentAction = from == "1",
            };
            //JsonSerializerSettings serializerSettings = new JsonSerializerSettings
            //{
            //    ContractResolver = new CamelCasePropertyNamesContractResolver(),
            //    DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind
            //};

            //return Content(JsonConvert.SerializeObject(res, serializerSettings));
            return View(res);
        }

        public async Task<IActionResult> CheckSolution(IFormFile file, int taskId, string framework)
        {            
            var task = _db.StudentTasks.Find(taskId);

            if (task == null)
            {
                var response1 = new
                {
                    status = "error",
                    data = "No task found!"
                };
                return Json(response1);
            }            

            if (!CanSend(task, curId))
            {
                var response2 = new
                {
                    status = "error",
                    data = "Can't send new solutions!"
                };
                return Json(response2);
            }
            
            if (file != null)
            {
                string dirPath = "c:/TemporaryTaskDownloads";

                dirPath += "/" + Guid.NewGuid().ToString();
                while (Directory.Exists(dirPath))
                {
                    dirPath += "/" + Guid.NewGuid().ToString();
                }

                var dir = Directory.CreateDirectory(dirPath);

                string fullZipFilePath = dirPath + "/" + file.FileName;
                string fullDirPath = dirPath + "/unpacked";

                // сохраняем файл в папку
                using (var fileStream = new FileStream(fullZipFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }
                ZipFile.ExtractToDirectory(fullZipFilePath, fullDirPath, true);

                //ищем файл проекта
                var pathToProj = FindProjectFile(dir);

                if (pathToProj == null)
                {
                    var response2 = new
                    {
                        status = "error",
                        data = "No project file found!"
                    };
                    return Json(response2);
                }

                //чистим от бинарных файлов
                CleanSolutionFiles(fullDirPath);

                //сжимаем очищенные файлы обратно в архив
                string newfullZipFilePath = dirPath + "/cleaned_" + file.FileName;
                ZipFile.CreateFromDirectory(fullDirPath, newfullZipFilePath);

                //записываем файл с архивом в базу
                var dataBytes = System.IO.File.ReadAllBytes(newfullZipFilePath);

                int fileId = _db.UploadFile(file.FileName, dataBytes);

                if (fileId == -1)
                {
                     var response2 = new
                    {
                        status = "error",
                        data = "Can't upload file to db!"
                    };
                    return Json(response2);
                }

                dir.Delete(true);

                //записываем новое решение в базу
                var solution = new Solution
                {
                    TaskId = taskId,
                    FrameworkType = framework,
                    FileId = fileId,
                    StudentId = curId,
                    ResultCode = ResultCode.NT,
                    Time = DateTime.Now,
                };

                var x = _db.Solutions.Add(solution);
                var beforeState = x.State;
                int r = _db.SaveChanges();
                var afterState = x.State;
                bool ok = beforeState == EntityState.Added && afterState == EntityState.Unchanged && r == 1;
            
                if (!ok)
                {
                    var response2 = new
                    {
                        status = "error",
                        data = "Error on writing solution to db!"
                    };
                    return Json(response2);
                }

                //если это первое решение - записываем как результат
                var studentResult = _db.StudentResults.Find(curId, taskId);

                if (studentResult is null)
                {
                    var newResult = new StudentResult
                    {
                        StudentId = curId,
                        TaskId = taskId,
                        SolutionId = solution.Id
                    };

                    var x1 = _db.StudentResults.Add(newResult);
                    beforeState = x1.State;
                    r = _db.SaveChanges();
                    afterState = x1.State;
                    ok = beforeState == EntityState.Added && afterState == EntityState.Unchanged && r == 1;

                    if (!ok)
                    {
                        var response2 = new
                        {
                            status = "error",
                            data = "Error on writing result to db!"
                        };
                        return Json(response2);
                    }
                }


                //запускаем тестирование посылая сообщение микросервису
                try
                {
                    var msgQueue = RabbitHutch.CreateBus(_config.MessageQueueInfo, _config.FrontEnd);

                    var request = new SolutionTestingRequest
                    {
                        SolutionId = solution.Id,
                    };
                    await msgQueue.SendAsync(_config.MessageQueueInfo.TestingQueueName, request);
                }
                catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException e)
                {
                    var response1 = new
                    {
                        status = "connectionError",
                        msg = "Can't connect to RabbitMQ!",
                        data = GetStudentTaskView(taskId, curId)
                    };
                    return Json(response1);
                }

                var response = new
                {
                    status = "success",
                    data = GetStudentTaskView(taskId, curId)
                };
                return Json(response);
            }

            var response3 = new
            {
                status = "error",
                data = "Error while sending file!"
            };
            return Json(response3);
        }

        public IActionResult CheckForUpdates(string ids, int taskId)
        {
            int[] solIds = JsonConvert.DeserializeObject<int[]>(ids);

            foreach(var id in solIds)
            {
                var s = _db.Solutions.Find(id);

                if(s != null && s.ResultCode != ResultCode.NT)
                {
                    var res = GetStudentTaskView(taskId, curId);
                    
                    //JsonSerializerSettings serializerSettings = new JsonSerializerSettings
                    //{
                    //    ContractResolver = new CamelCasePropertyNamesContractResolver(),
                    //    DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind
                    //};

                    //var data = JsonConvert.SerializeObject(res, serializerSettings);

                    var response1 = new
                    {
                        status = "success",
                        //data = "./SendStudentSolution?id=" + taskId.ToString()
                        data = res
                    };
                    return Json(response1);
                }
            }

            var response2 = new
            {
                status = "error",
            };
            return Json(response2);
        }

        public async Task SSE(string ids, int taskId)
        {
            await SSEMethod(ids, taskId, curId);
        }

        void CleanSolutionFiles(string dir)
        {
            string gitignorePath = _pathToConfigDir + "\\.gitignore";
            var patterns = System.IO.File.ReadAllLines(gitignorePath).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();


            var dirsToDelete = new List<string>();
            var dirs = new string[] { "bin", "obj", ".vs" };

            foreach(var dirr in dirs)
            {
                dirsToDelete.AddRange(Directory.GetDirectories(dir, dirr, SearchOption.AllDirectories));
            }

            foreach(var dirr in dirsToDelete)
            {
                Directory.Delete(dirr, true);
            }

            var filesToDelete = new List<string>();
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
                filesToDelete.AddRange(files);
            }

            foreach (var file in filesToDelete)
            {
                System.IO.File.Delete(file);
            }
        }       
    }
}
