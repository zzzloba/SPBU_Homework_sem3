﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MyThreadPool
{
    /// <summary>
    /// Класс, реализующий пул потоков
    /// </summary>
    public class MyThreadPool
    {
        /// <summary>
        /// Список потоков в пуле
        /// </summary>
        private List<Thread> threads;

        /// <summary>
        /// Количество работающих потоков
        /// </summary>
        private int runningThreads;

        /// <summary>
        /// Очередь задач на выполнение
        /// </summary>
        private ConcurrentQueue<Action> taskQueue;

        /// <summary>
        /// Возвращает токен, отвечающий за отмену работы пула
        /// </summary>
        private CancellationTokenSource cts;

        /// <summary>
        /// Указывает на то, работает ли пул
        /// </summary>
        private bool isWorking
            => !cts.IsCancellationRequested;

        /// <summary>
        /// Объект, с помощью которого подаём сигналы потокам
        /// при добавлении в очередь очередной задачи
        /// </summary>
        private AutoResetEvent taskAutoQueryWaiter;

        /// <summary>
        /// Объект, с помощью которого подаём сигнал
        /// о готовности к завершению работы пула
        /// </summary>
        private AutoResetEvent readyToCloseWaiter;

        /// <summary>
        /// Конструктор, создающий пул с фиксированным числом потков
        /// </summary>
        public MyThreadPool(int numberOfThreads)
        {
            threads = new List<Thread>();
            taskQueue = new ConcurrentQueue<Action>();
            taskAutoQueryWaiter = new AutoResetEvent(false);
            readyToCloseWaiter = new AutoResetEvent(false);
            cts = new CancellationTokenSource();

            runningThreads = numberOfThreads;

            for (var i = 0; i < numberOfThreads; ++i)
            {
                threads.Add(new Thread(() =>
                {
                    while (true)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            if (taskQueue.Count == 0)
                            {
                                break;
                            }
                        }

                        // Выполняем то, что появляется в очереди
                        if (taskQueue.TryDequeue(out var calculateTask))
                        {
                            if (!taskQueue.IsEmpty)
                            {
                                taskAutoQueryWaiter.Set();
                            }

                            calculateTask();
                        }
                        else
                        {
                            // Переводим исполнителя в состояние ожидания,
                            // пока в очередь не добавится MyTask
                            taskAutoQueryWaiter.WaitOne();
                        }
                    }

                    // Фиксируем тот факт, что поток закончил работу
                    Interlocked.Decrement(ref runningThreads);

                    // Подаём сигнал о том, что можно заканчивать работу
                    readyToCloseWaiter.Set();
                }));

                threads[i].Start();
            }
        }

        /// <summary>
        /// Метод, с помощью котрого можно узнать о состоянии потока
        /// </summary>
        public bool IsWorking()
            => isWorking;

        /// <summary>
        /// Завершает работу потоков в пуле
        /// Если есть незавершённые задачи, даём им завершиться
        /// </summary>
        public void Shutdown()
        {
            if (!isWorking)
            {
                throw new MyThreadPoolNotWorkingException("Pool is already not working.");
            }

            cts.Cancel();

            // Будим один из потоков
            taskAutoQueryWaiter.Set();

            // Ждём, пока потоки по одному доделают свои дела
            while (true)
            {
                // Ждём, пока поток не просигналит о том, что он завершился
                readyToCloseWaiter.WaitOne();
                // Закончивший работу поток будит следующий поток
                // По цепочке так завершат работу все потоки
                taskAutoQueryWaiter.Set();

                // Если все закончили работу, выходим
                if (runningThreads == 0)
                {
                    break;
                }
            }

            taskQueue = null;
        }

        /// <summary>
        /// Ставит в очередь задачу, выполняющую переданное вычисление
        /// </summary>
        public IMyTask<TResult> QueueTask<TResult>(Func<TResult> supplier)
            => QueueMyTask(new MyTask<TResult>(supplier, this));

        /// <summary>
        /// Ставит в очередь переданную задачу MyTask
        /// </summary>
        private IMyTask<TResult> QueueMyTask<TResult>(MyTask<TResult> task)
        {
            if (!cts.IsCancellationRequested)
            {
                QueueAction(task.Calculate);

                return task;
            }

            throw new MyThreadPoolNotWorkingException("Пул потоков не работает, невозможно поставить новую задачу в очередь на исполнение.");
        }

        private Action QueueAction(Action task)
        {
            // Добавляем задачу в очередь на исполнение
            taskQueue.Enqueue(task);
            // Даём исполнителю задачи сигнал, если он в ожидании
            taskAutoQueryWaiter.Set();

            return task;
        }

        /// <summary>
        /// Класс, реализующий интерфейс IMyTask
        /// </summary>
        private class MyTask<TResult> : IMyTask<TResult>
        {
            /// <summary>
            /// Объект, предоставляющий вычисление
            /// </summary>
            private Func<TResult> supplier;

            /// <summary>
            /// Результат вычисления
            /// </summary>
            private TResult result;

            /// <summary>
            /// Флаг, указывающий на то, посчитан ли результат
            /// </summary>
            public bool IsCompleted { get; private set; } = false;

            /// <summary>
            /// Объект для блокировки задачи
            /// </summary>
            private object taskLock = new object();

            /// <summary>
            /// Объект, с помощью которого подаём сигналы потокам
            /// о готовности результата задачи
            /// </summary>
            private ManualResetEvent resultWaiter;

            /// <summary>
            /// Пул потоков, в котором выполняется MyTask
            /// </summary>
            private MyThreadPool pool;

            /// <summary>
            /// Очередь для задач из ContinueWith
            /// </summary>
            private Queue<Action> taskQueue;

            /// <summary>
            /// Блокировщик очереди задач из ContinueWith
            /// </summary>
            private object taskQueueLock = new object();

            /// <summary>
            /// Обработчик брошенных исключений
            /// </summary>
            private Exception exceptionHandler;

            public TResult Result
            {
                get
                {
                    // Здесь ждём, пока считается результат
                    // ManualResetEvent даст сингал всем ждущим потокам
                    resultWaiter.WaitOne();

                    if (exceptionHandler == null)
                    {
                        return result;
                    }

                    throw exceptionHandler;
                }
                private set
                    => result = value;
            }

            /// <summary>
            /// При создании MyTask требуем предоставить объект для вычислений,
            /// а также указать, в каком пуле таск будет исполняться
            /// </summary>
            public MyTask(Func<TResult> supplier, MyThreadPool pool)
            {
                this.supplier = supplier;
                this.pool = pool;
                resultWaiter = new ManualResetEvent(false);
                taskQueue = new Queue<Action>();
            }

            /// <summary>
            /// Само вычисление
            /// </summary>
            public void Calculate()
            {
                // Попытка выполнить задачу и в случае чего перехватить исключение
                try
                {
                    result = supplier();
                    supplier = null;
                }
                catch (Exception supplierException)
                {
                    exceptionHandler = supplierException;
                }
                finally
                {
                    lock (taskLock)
                    {
                        if (exceptionHandler == null)
                        {
                            IsCompleted = true;
                        }

                        // Уведомляем о том, что выполнение задачи завершено
                        resultWaiter.Set();

                        // Кидаем в пул все задачи,
                        // которые должны следовать за текущей;
                        // Так они смогут быть исполнены любым из свободных потоков, 
                        // не только тем, в котором выполняли текущие вычисления
                        lock (taskQueueLock)
                        {
                            while (taskQueue.Count != 0)
                            {
                                pool.QueueAction(taskQueue.Dequeue());
                            }
                        }
                    }
                }
            }

            public IMyTask<TNewResult> ContinueWith<TNewResult>(Func<TResult, TNewResult> supplier)
            {
                var nextTask = new MyTask<TNewResult>(() => supplier(Result), pool);

                lock (taskLock)
                {
                    lock (taskQueueLock)
                    {
                        if (!IsCompleted)
                        {
                            taskQueue.Enqueue(nextTask.Calculate);
                            return nextTask;
                        }
                    }
                }

                return pool.QueueMyTask(nextTask);
            }
        }
    }
}
